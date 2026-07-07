# Release 版本与更新日志设计

## 目标

将 GitLab rolling release 更新机制替换为不可变的通道 release，优化 stable/nightly 版本号生成规则，从 commit body 自动生成双语 changelog，并让客户端更新界面按版本分页展示所有较新的更新日志。

## 当前状态

当前 GitLab CI 会构建安装包、上传 Generic Package 文件、创建或刷新 `stable-release` 和 `nightly-release` 两个 rolling release，并为最新构建写入一个 `sourcegit-update.json` manifest。客户端只读取当前通道的一个固定 release tag，在该 release 中查找 `sourcegit-update.json`，再把 manifest 版本与 `Models.BuildInfo` 比较。

本设计明确不兼容旧客户端。旧客户端只认识 `stable-release` 和 `nightly-release` 的行为可以删除。

## 版本号规则

stable release 只允许手动触发。CI 暴露 `STABLE_VERSION` 变量。

如果用户设置了 `STABLE_VERSION`，它必须匹配 `MAJOR.MINOR.PATCH`，例如 `1.0.0`。CI 会把它与现有最新 stable 基础版本比较；如果用户指定的版本号不大于最新 stable 基础版本，CI 必须在打包前失败退出。

如果 `STABLE_VERSION` 为空，CI 读取现有 `stable-*` release tag，提取最新基础版本，并把 patch 加一。如果没有 stable 历史版本，则从 `1.0.0` 开始。

最终 stable release 版本为：

```text
stable-<MAJOR.MINOR.PATCH>.<CI_COMMIT_SHORT_SHA>
```

示例：

```text
stable-1.0.0.2d13ad8b
```

schedule 触发的 nightly release 使用构建日期：

```text
nightly-YYYY.MM.DD.<CI_COMMIT_SHORT_SHA>
```

手动触发的 nightly release 增加每日自增序号：

```text
nightly-YYYY.MM.DD.<CI_COMMIT_SHORT_SHA>(N)
```

`N` 每天从 `1` 开始。CI 从当天已有的手动 nightly release tag 中计算下一个序号，日期变化后自然重置。

CI 使用三类版本变量，避免展示版本和包管理版本混用：

- `BASE_VERSION`：可比较的基础版本，例如 stable 的 `1.0.0` 或 nightly 的 `2026.06.26`。
- `RELEASE_VERSION`：用户可见的不可变 release 版本，也是 GitLab release tag，例如 `stable-1.0.0.2d13ad8b` 或 `nightly-2026.06.26.2d13ad8b(1)`。
- `PACKAGE_VERSION`：适合文件系统、包路径和包管理元数据使用的安全版本号，用于 Generic Package 路径、安装包文件名和 Linux 包元数据。

`PACKAGE_VERSION` 必须适合作为文件名、Generic Package version 和 Linux 包管理元数据，因此使用数字开头且不含括号或连字符的格式。

stable release 的 `PACKAGE_VERSION` 为：

```text
<MAJOR.MINOR.PATCH>.stable.<CI_COMMIT_SHORT_SHA>
```

schedule nightly release 的 `PACKAGE_VERSION` 为：

```text
YYYY.MM.DD.nightly.<CI_COMMIT_SHORT_SHA>
```

manual nightly release 的 `PACKAGE_VERSION` 为：

```text
RELEASE_VERSION=nightly-2026.06.26.2d13ad8b(1)
PACKAGE_VERSION=2026.06.26.nightly.2d13ad8b.1
```

`SourceGitUpdateVersion` 使用 `RELEASE_VERSION`，因为它属于展示和比较元数据。包上传路径、包文件名和包管理元数据使用 `PACKAGE_VERSION`。

## Release 存储

CI 不再创建或更新 `stable-release` 和 `nightly-release`。

每次构建只创建一个不可变 GitLab release：

```text
stable-1.0.0.2d13ad8b
nightly-2026.06.26.2d13ad8b
nightly-2026.06.26.2d13ad8b(1)
```

release 保留策略：

- 保留最新 30 个 stable release。
- 保留最新 90 个 schedule nightly release。
- 保留最新 10 个 manual nightly release。
- manual nightly release 是临时验证包，不参与客户端更新，不计入 90 个 schedule nightly 保留数量。
- 历史 release 删除后，再删除对应的旧 Generic Package 版本。

## Manifest 文件

每个不可变 release 都包含一个 `sourcegit-update.json` 资源，以及各平台安装包资源。

manifest 保留现有字段，并使用新的版本字符串：

```json
{
  "version": "stable-1.0.0.2d13ad8b",
  "baseVersion": "1.0.0",
  "packageVersion": "1.0.0.stable.2d13ad8b",
  "channel": "stable",
  "commit": "...",
  "publishedAt": "2026-06-26T00:00:00Z",
  "releaseNotes": "- English item\n----------------\n- Chinese item",
  "assets": []
}
```

nightly 的 `baseVersion` 是日期版本，例如 `2026.06.26`。

## Changelog 生成

stable release 和 schedule nightly release 使用同一个 commit body 解析器生成 changelog。manual nightly release 是临时验证包，不生成 changelog，也不参与 changelog 范围计算。手动覆盖 release notes 不在本设计范围内。

stable release 生成 changelog 时，CI 查找上一个 stable release，并使用 `git log` 获取从上一个 stable release commit 之后到当前 commit 为止的提交范围。

schedule nightly release 生成 changelog 时，CI 只查找上一个 schedule nightly release，并使用 `git log` 获取从上一个 schedule nightly release commit 之后到当前打包节点为止的提交范围。manual nightly release 即使在两个 schedule nightly release 之间产生，也不能影响这个范围。

每个 commit 的解析流程：

1. 读取 commit body。如果 body 只有一行且包含字面量 `\n`，先把 `\n` 替换成真实换行再解析，用于兼容之前错误生成的单行 commit message。
2. 使用一整行严格等于 `----------------` 的分隔符拆分英文和中文区域。
3. 每一行以 `- ` 开头的内容都作为 changelog 候选项。
4. 丢弃以 `(NO CHANGELOG)` 结尾的候选项。
5. 保留剩余英文和中文条目，并保持原始顺序。

最终 release notes 格式为：

```text
- English item 1
- English item 2
----------------
- 中文条目 1
- 中文条目 2
```

如果没有任何面向用户的 changelog 条目，使用默认内容：

```text
- Maintenance update.
----------------
- 维护更新。
```

## Commit Skill 更新

更新 `.agents/skills/codex-commit/SKILL.md`，让未来提交能稳定产出适合 changelog 的 commit body。

skill 必须要求 agent：

- 写真实多行 commit body，不要把换行错误写成字面量 `\n`。
- 在执行 `git commit` 前检查传给 `git commit -F` 的实际 message 文件或字符串；如果发现应该换行的位置出现字面量 `\n`，必须修复后再提交。
- 主动判断每一条 body bullet 是否面向用户。
- 只要 commit 包含用户可感知改动，即使只有一条，也必须写入双语 body bullet，避免 changelog 只能看到 subject 而漏掉内容。
- 面向用户的改动不加标记，让 CI 自动收入 changelog；功能修改、交互改动、bug 修复和大性能优化通常不加标记。
- 对重构、测试、依赖整理、内部 CI 机制、文档流程维护、小性能调优等内部改动追加 `(NO CHANGELOG)`。
- 内部 CI 改动默认不进入 changelog；如果 agent 判断某个发布流程变化会被用户明确感知，可以不加 `(NO CHANGELOG)`。
- 英文和中文 bullet 区域保持语义一一对应。

## Client 更新流程

`Models.UpdateChecker` 不再读取固定 rolling release tag。它改为请求 GitLab 项目的 release 列表，并按通道过滤。

checker 使用 `per_page=100` 请求 release 列表，按 `released_at desc` 排序分页读取。它持续读取，直到 GitLab 返回空页，或已经收集到当前通道保留上限数量的有效 release：stable 30 个，schedule nightly 90 个。格式错误的 tag 和 manual nightly tag 不计入保留上限。

stable release tag 必须匹配：

```text
^stable-(\d+)\.(\d+)\.(\d+)\.[0-9a-f]+$
```

客户端可见的 nightly release tag 只包含 schedule nightly，必须匹配：

```text
^nightly-(\d{4})\.(\d{2})\.(\d{2})\.[0-9a-f]+$
```

manual nightly tag 包含括号序号，例如 `nightly-2026.06.26.2d13ad8b(1)`。checker 必须忽略这类 tag，不下载它们的 manifest，不展示它们的 changelog，也不把它们作为安装候选。

checker 按解析后的版本排序，而不是按字符串字典序排序。

stable 排序先比较 `MAJOR`，再比较 `MINOR`，再比较 `PATCH`，最后用发布时间做平局处理。

nightly 排序只处理 schedule nightly release，先比较日期，再用发布时间做平局处理。manual nightly release 不进入排序集合。

当用户选择的更新通道与 `BuildInfo.Channel` 相同，checker 下载所有比当前构建更新的 manifest。stable 使用解析后的语义版本比较，并用发布时间作为平局处理。nightly 只比较 schedule nightly release，并使用发布时间作为同日期平局处理。

当用户选择的更新通道与 `BuildInfo.Channel` 不同，或当前构建版本无法按所选通道规则解析时，checker 把当前构建视为不可比较。manual nightly 构建因为版本号带括号序号，按客户端更新规则视为不可比较。此时 checker 提供所选通道的最新 stable 或 schedule nightly release，并保留该通道保留范围内的 changelog 页面。

checker 选择最新 manifest 作为安装候选，但保留所有相关新版本的 manifest 用于 changelog 展示。

installer 仍然只安装最新安装包，不逐个安装中间版本。

## 更新界面

`Models.UpdateAvailable` 增加 changelog 页面集合。每一页包含：

- 版本号
- 发布时间
- release notes

更新弹窗每页显示一个 release 的 changelog，默认打开最新 release 页面。

在 changelog 区域附近增加上一页和下一页按钮：

- 上一页显示更旧 release 的 changelog。
- 下一页显示更新 release 的 changelog。
- 到达两端时禁用对应按钮。

changelog 区域保留垂直滚动条。默认高度应至少能舒适显示 15 条 changelog 条目，超过后再滚动。

安装按钮仍然安装最新可用版本。

忽略按钮作用于最新安装候选，而不是当前正在浏览的 changelog 页面。忽略更新时记录最新候选的通道和版本号。

## 错误处理

CI 在以下场景必须尽早失败：

- stable release 由 schedule 触发。
- `STABLE_VERSION` 格式错误。
- `STABLE_VERSION` 不大于最新 stable 基础版本。
- release 或 package 上传失败。
- changelog 生成无法查询必要的 Git 历史。

客户端更新检查把格式错误的历史 release tag 视为可忽略数据。只有当某个候选 release 确实比当前版本更新，但它的 manifest 无法下载或解析时，才把更新检查视为失败。

## 测试

CI 脚本测试应覆盖：

- 手动 stable 显式指定合法版本。
- 手动 stable 未指定版本且存在 stable 历史版本。
- 手动 stable 指定版本等于或低于最新 stable 时失败。
- schedule nightly 版本格式。
- manual nightly 序号从 `1` 开始，并按日期重置。
- manual nightly 不生成 changelog。
- schedule nightly 的 changelog 范围忽略中间产生的 manual nightly，只从上一个 schedule nightly release 计算。
- changelog 提取跳过 `(NO CHANGELOG)`。
- changelog 提取能兼容包含字面量 `\n` 的 commit body。
- 保留策略保留 30 个 stable release、90 个 schedule nightly release 和 10 个 manual nightly release。

客户端测试应覆盖：

- stable 版本解析和排序。
- nightly 版本解析和排序。
- 客户端忽略 manual nightly release tag。
- 多个较新 manifest 生成多个 changelog 页面。
- 安装时选择最新资源。
- 更新弹窗分页按钮的边界状态。
