# GitLab 自更新设计

## 目标

为 SourceGit 增加基于 GitLab 的发布流水线和 Windows 自动更新能力。Windows 端移植现有 `GameUpdater` 的目录替换方案，生成专用的 `sourcegit-updater.exe`；macOS 和 Linux 暂时只提示更新并跳转到下载页面。

## 背景

SourceGit 目前通过 `https://sourcegit-scm.github.io/data/version.json` 检查更新，发现新版本后打开 GitHub release 页面。它不会自动下载、暂存或安装更新。

现有通用更新器位于 `F:\work\hygames\Tools\GameUpdater`，解决了 Windows 上运行中的程序无法替换自身可执行文件的问题。但它当前有游戏项目假设：会寻找 Unity 风格游戏目录，保留 `LocalPersistentData`，识别 `GameUpdater.exe`，并在替换完成后启动游戏主程序。SourceGit 需要复用它的目录交换、重试和恢复机制，但包结构、保留目录和启动目标都要 SourceGit 化。

本仓库 GitLab remote 是 `git@gitlab.zjhuayu.top:all/sourcegit.git`。对应 Web/API 地址使用 `http://gitlab.zjhuayu.top`。GitLab runner 可能只有 Linux 服务器，因此 GitLab CI 必须能从 Linux 上完成跨平台打包。GitLab 产出的 Windows 自动更新包可以使用 `DisableAOT=true`，避免依赖 Windows runner。

## 外部约束

- SourceGit 客户端和下载后的配置文件中不能内置 GitLab token。
- GitLab release 和 package 资产需要允许匿名读取；如果不能匿名读取，客户端只能跳转浏览器下载页。
- CI 使用 GitLab 提供的 `CI_JOB_TOKEN` 上传 package 并调用 release API。如果自建 GitLab 实例不允许 `CI_JOB_TOKEN` 列出或删除 package 版本，nightly 清理步骤可以额外使用 GitLab CI/CD 变量中配置的 masked `GITLAB_RELEASE_TOKEN`。
- 本次只有 Windows 支持自动安装更新。
- macOS 和 Linux 检测到更新后只打开 release 或下载页面。

## Release 模型

使用两个滚动渠道 release 作为客户端入口：

- `stable-release`：当前稳定渠道。
- `nightly-release`：当前 nightly 渠道。

这两个 release 是“渠道指针”，不是不可变版本历史。它们的 release asset links 指向当前渠道最新的 package registry 文件和渠道 manifest。

稳定版还会创建不可变的历史 release：

- 示例：`stable-v2026.13`。
- 历史 stable release 不会被后续 stable 发布覆盖。
- 历史 release 指向发布当时同一批 package registry 文件。

Nightly 不为每次构建创建一个 GitLab Release。每次 nightly 构建上传一个带版本号的 package registry 条目，然后更新滚动的 `nightly-release`。流水线只保留最新 30 个 nightly package 版本，删除更旧的 nightly package 版本。

这个模型能让客户端逻辑保持简单，同时保留 stable 历史，并限制 nightly 存储增长。

## 版本模型

Stable 构建使用仓库 `VERSION` 文件作为用户可见版本：

```text
2026.13
```

Nightly 构建追加日期、pipeline IID 和提交短号：

```text
2026.13-nightly.20260623.<pipeline_iid>.<short_sha>
```

CI 还会通过 assembly metadata 把更新元数据写入 SourceGit：

- `SourceGitChannel`：`stable`、`nightly` 或 `local`。
- `SourceGitVersion`：完整用户可见版本号。
- `SourceGitBaseVersion`：`VERSION` 文件中的基础版本号。
- `SourceGitPipelineIid` 和 `SourceGitCommit`：CI 元数据，有则写入。

客户端通过这些内置元数据和远端 manifest 比较版本。客户端不能把滚动 release tag 当作真实应用版本，因为 `stable-release` 和 `nightly-release` 是固定渠道名。

## 更新 Manifest

每个 package 版本都包含一个 `sourcegit-update.json` manifest，并作为 release asset link 暴露给客户端。

Manifest 结构：

```json
{
  "version": "2026.13-nightly.20260623.1234.ad37afd",
  "baseVersion": "2026.13",
  "channel": "nightly",
  "commit": "ad37afdb9...",
  "pipelineId": "123456",
  "pipelineIid": "1234",
  "publishedAt": "2026-06-23T00:15:00Z",
  "releaseNotes": "由流水线元数据或 release notes 生成。",
  "assets": [
    {
      "runtime": "win-x64",
      "kind": "self-update-zip",
      "fileName": "sourcegit_2026.13-nightly.20260623.1234.ad37afd.win-x64.zip",
      "url": "http://gitlab.zjhuayu.top/.../sourcegit_2026.13-nightly.20260623.1234.ad37afd.win-x64.zip",
      "sha256": "..."
    }
  ]
}
```

必须包含的资产：

- `win-x64` 自动更新 zip，必须包含 `sha256`。
- `win-arm64` 自动更新 zip，必须包含 `sha256`。
- `osx-x64` 和 `osx-arm64` 的 macOS zip 包。
- `linux-x64` 和 `linux-arm64` 的 Linux AppImage、deb、rpm 包。

更新对话框展示 manifest 中的 `releaseNotes`。如果 manifest 缺失或格式错误，手动检查更新时显示失败；启动自动检查时静默失败。

## GitLab CI/CD

新增 `.gitlab-ci.yml`，包含两个入口：

- 定时 nightly pipeline：
  - 在 `huayu` 分支运行。
  - 计划每天凌晨运行。
  - package version 使用 `VERSION-nightly.YYYYMMDD.CI_PIPELINE_IID.CI_COMMIT_SHORT_SHA`。
  - 更新 `nightly-release`。
  - 删除超过最新 30 个的 nightly package 版本。

- 手动 stable pipeline：
  - 通过 GitLab Run pipeline 手动触发。
  - package version 使用 `VERSION` 文件。
  - 更新 `stable-release`。
  - 创建或保留不可变的 `stable-vVERSION` 历史 release。
  - 不删除 stable package 版本。

流水线阶段：

1. 拉取 submodule，restore 并 build SourceGit。
2. 使用 `DisableAOT=true` 为目标 runtime 发布 SourceGit。
3. 发布 Windows runtime 的 `sourcegit-updater.exe`。
4. 按 updater 规范打 Windows 自动更新 zip。
5. 使用现有打包脚本或其 GitLab 适配版打 macOS 和 Linux 包。
6. 生成 SHA-256 校验和和 `sourcegit-update.json`。
7. 上传所有文件到 GitLab Generic Package Registry。
8. 为滚动渠道 release upsert asset links；stable 还要为不可变历史 release upsert 同一批 asset links。
9. 清理超过最新 30 个的 nightly package 版本。

CI 使用 GitLab release、release links 和 generic packages API。GitLab 支持创建/更新 release、维护 release asset links，以及从 CI job 上传 package。Release asset link 可以提供稳定的下载入口。

## 包结构

Windows 自动更新 zip 结构：

```text
sourcegit_<version>.win-x64.zip
├── sourcegit-updater.exe
└── SourceGit/
    ├── SourceGit.exe
    ├── SourceGit.dll 或 native publish 输出文件
    └── ...
```

这保持了当前 Windows zip 的使用习惯：用户仍然可以解压得到顶层 `SourceGit` 目录。Updater 位于 `SourceGit` 目录旁边，这样它可以在 SourceGit 退出后重命名并替换整个 `SourceGit` 目录。

Updater 同时支持 SourceGit 显式传参：

```text
sourcegit-updater.exe --package <zip> --target <current-app-dir> --exe SourceGit.exe
```

显式参数可以避免完全依赖目录命名；规范包结构仍然是主要分发结构。

便携模式数据通过保留应用目录下的 `data` 子目录实现：

```text
SourceGit/data
```

## SourceGit 客户端流程

偏好设置：

- 新增更新渠道设置：Stable 或 Nightly。
- 默认渠道为 Stable。
- 保留现有“启动时检查更新”设置。
- 保留忽略版本行为，但按渠道和版本记录，避免忽略一个 nightly 后隐藏 stable 更新。

检查流程：

1. 根据偏好设置选择渠道 tag：`stable-release` 或 `nightly-release`。
2. 请求 GitLab release JSON。
3. 找到并下载 `sourcegit-update.json` 资产。
4. 用 manifest 版本和当前构建内置元数据比较。
5. 如果当前版本已是最新：
   - 手动检查显示“已是最新版本”。
   - 启动检查静默返回。
6. 如果存在更新：
   - 弹出更新对话框，显示渠道、版本、发布时间和更新说明。
   - Windows 显示“更新”操作。
   - macOS/Linux 显示“下载”操作。

Windows 安装流程：

1. 根据当前进程架构选择 manifest 中的 `win-x64` 或 `win-arm64` 资产。
2. 下载 zip 到 `Native.OS.DataDir/updates` 下的暂存目录。
3. 校验 manifest 中的 SHA-256；Windows 自动更新资产缺少 `sha256` 时阻止安装。
4. 从 zip 中只解压 `sourcegit-updater.exe` 到 `Native.OS.DataDir/updates/<version>/` 这类用户可写暂存目录。
5. 从暂存目录启动 `sourcegit-updater.exe --package <zip> --target <app-dir> --exe SourceGit.exe`。
6. SourceGit 退出。
7. Updater 替换目录并重新启动 `SourceGit.exe`。

macOS/Linux 流程：

- 打开滚动 release 页面或选中的资产 URL。
- 不尝试原地替换。

## Updater 工程

新增 Windows-only 工程：

```text
src/SourceGit.Updater/SourceGit.Updater.csproj
```

从 `F:\work\hygames\Tools\GameUpdater` 适配这些文件：

- `Program.cs`
- `SourceGitUpdater.cs`
- `UpdaterUI.cs`
- `Win32UI.cs`
- `FileUtils.cs`
- `app.manifest`
- `icon.ico` 或复用 SourceGit app icon 作为嵌入资源。

相对 GameUpdater 的必改点：

- namespace 和二进制名称改为 SourceGit 专用。
- UI 标题和消息改成 SourceGit 更新器，不再出现游戏文案。
- `KeepSubDir` 改为 `data`。
- 识别 `SourceGit.exe`，不再识别 Unity `*_Data` 目录。
- 从包根目录更新 `sourcegit-updater.exe`，不再处理 `GameUpdater.exe`。
- 清理旧的 `sourcegit-updater*.exe` 文件。
- 支持 SourceGit 传入的显式命令行参数。
- 保留崩溃安全恢复逻辑：如果替换中断，尽可能恢复 backup 目录。

Manifest 权限：

- 保留 `requireAdministrator`，以便 SourceGit 安装在 `Program Files` 等受保护目录时仍可替换文件。
- 这意味着便携更新也可能弹 UAC，但换来的是能更新受保护安装目录。
- 如果未来产品决策希望便携包不弹 UAC，可以把 updater 改成 `asInvoker`，并让 SourceGit 提前检测目标目录写权限。

## 错误处理

- 启动自动检查不显示网络错误。
- 手动检查显示清晰的失败消息。
- 下载失败不会修改现有安装。
- SHA-256 缺失或校验失败时删除下载 zip 并报告失败。
- Updater 替换失败时显示重试/取消窗口。
- 如果 updater 无法从中断状态恢复 backup，应报告路径并退出，不继续删除残留目录。
- 如果 GitLab 资产需要鉴权，客户端提示 release/package 需要匿名可读或改用浏览器下载。

## 安全

- SourceGit 二进制、更新 manifest 和客户端配置中都不写入 client token、deploy token 或 private access token。
- 当前 GitLab Web/API 地址是 `http://gitlab.zjhuayu.top`。由于 manifest 和 zip 都通过 HTTP 获取，SHA-256 只能发现传输损坏或非同源文件错误，不能防御能同时篡改 manifest 与 zip 的主动中间人攻击。本次安全边界是内网 GitLab 公开下载、不在客户端保存 token、安装前强制校验 manifest 中记录的 zip SHA-256；如果未来要覆盖不可信网络，应增加 HTTPS 或签名 manifest/package。
- 未来如果 GitLab 切到 HTTPS，客户端只需要调整配置常量或 manifest URL，不改变更新流程。
- Zip 解压必须拒绝逃逸目标目录的路径。
- Updater 只保留配置的 `data` 目录，不把旧安装目录中的任意文件合并进新版本。
- package 清理只在 CI 运行，绝不能由客户端执行。

## 验证

实现后需要执行：

- `dotnet build SourceGit.slnx`
- 使用 `DisableAOT=true` 发布 `win-x64` SourceGit。
- 发布 `src/SourceGit.Updater/SourceGit.Updater.csproj` 的 `win-x64` 版本。
- 构建示例 Windows 更新 zip，并检查包含 `sourcegit-updater.exe` 和 `SourceGit/SourceGit.exe`。
- 用临时目录跑 updater 本地冒烟测试：
  - 旧 `SourceGit` 目录包含 `data` 文件夹和旧版本 marker 文件。
  - 更新 zip 包含新 `SourceGit` 目录。
  - updater 能替换文件、保留 `data`，并能启动或解析目标可执行文件路径。
- 如果改动了更新 UI 本地化 key，运行 localization check。
- Review `.gitlab-ci.yml` 语法和 shell 命令。

## 不在本次范围

- macOS/Linux 原地自动更新。
- 代码签名或 notarization。
- 增量更新。
- SourceGit 客户端访问私有 release 下载。
- GitLab releases/package registry 之外的新更新服务。
- 修改 GitHub release workflow，除非为了避免包名冲突必须调整。

## 参考

- GitLab Project Release API: https://docs.gitlab.com/api/releases/
- GitLab Release Links API: https://docs.gitlab.com/api/releases/links/
- GitLab Generic Package Registry: https://docs.gitlab.com/user/packages/generic_packages/
- GitLab Release asset permanent links: https://docs.gitlab.com/user/project/releases/release_fields/
