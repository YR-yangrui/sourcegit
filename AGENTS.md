# SourceGit 项目地图

本文件用于帮助 AI 快速理解 SourceGit 的架构、功能分布和关键入口。它不是个人开发习惯清单；如果需要任务流程、提交方式或临时约束，以用户提示和更具体的文档为准。

## 1. 项目定位

SourceGit 是一个跨平台 Git 图形客户端，使用 .NET 10 + Avalonia 12 构建。它的核心职责是把 Git 命令、仓库状态、提交历史、工作区变更、diff、分支、标签、stash、submodule、worktree、LFS、GitFlow 等能力组织成桌面 GUI。

运行时有几个重要外部边界：

- Git 可执行文件：大多数仓库操作最终通过 `src/Commands/` 调用 Git。
- 操作系统：窗口、数据目录、终端、文件管理器、外部工具由 `src/Native/` 适配。
- 用户数据：全局偏好存储在应用数据目录，仓库级设置存储在 Git common dir 下的 `sourcegit.settings` 等文件。
- Avalonia 资源：主题、图标、字体、多语言文案和样式由 `src/Resources/` 统一承载。

## 2. 启动入口与运行模式

应用入口在 `src/App.axaml.cs`，不是单独的 `Program.cs`。

`App.Main` 先初始化数据目录和诊断系统，然后根据参数进入不同模式：

- 普通 GUI：创建 `ViewModels.Launcher`，显示 `Views.Launcher`。
- File history：`--history <path>`，打开文件或目录历史窗口。
- Blame：`--blame <file>`，打开 blame 查看器。
- Core editor：`--core-editor <file>`，作为 Git commit message 编辑器。
- Askpass：通过 `SOURCEGIT_LAUNCH_AS_ASKPASS=TRUE` 作为 SSH askpass 窗口。
- Interactive rebase 辅助：`--rebase-todo-editor` 和 `--rebase-message-editor`，读写 rebase 中间文件。

普通 GUI 模式还会处理：

- 单实例 IPC：第二个实例把路径发送给第一个实例。
- 外部工具发现：终端、文件管理器、merge/diff tool 等。
- 头像管理、偏好加载、可选更新检查。

## 3. 总体架构

SourceGit 是典型的 Avalonia MVVM 桌面应用。AI 查代码时可按下面的依赖方向理解：

- `App.axaml.cs`：启动、资源、主题、本地化、全局命令；创建主窗口并进入对应运行模式。
- `Views/*.axaml`：界面、控件、窗口、弹窗；主要绑定 `ViewModels`。
- `ViewModels/*.cs`：页面状态、用户操作、异步刷新；调用 `Commands`、读取/更新 `Models`，必要时访问 `Native` 或 `AI`。
- `Commands/*.cs`：Git 命令封装和输出解析；依赖 `Native.OS.GitExecutable`，并向 `Diagnostics` 写命令诊断。
- `Models/*.cs`：领域模型、设置、状态、缓存、watcher、工具类型；部分模型也会写诊断事件。
- `Native/*.cs`：Windows/macOS/Linux 差异封装；被启动流程、命令执行和 UI 工具入口使用。
- `Resources/*`：主题、图标、字体、多语言、语法文件；由 `App.axaml` 和 Views 引用。
- `Diagnostics/*.cs`：事件、性能、崩溃、Perfetto trace；被 App、Commands、Models 等横向调用。
- `AI/*.cs`：OpenAI/Azure OpenAI 服务配置和聊天工具；由 AI 助手、提交信息相关 ViewModel 调用。

核心依赖方向：

- `Views` 主要绑定 `ViewModels`，少量 code-behind 用于 UI 行为。
- `ViewModels` 是功能编排层，负责响应用户操作、调用 `Commands`、更新 UI 状态。
- `Commands` 是 Git 命令边界，负责构造参数、启动进程、读取输出、解析结果。
- `Models` 放领域结构、设置、枚举、缓存和轻量服务。
- `Native` 封装平台差异，避免业务层直接分散判断 OS。
- `Resources` 统一管理 UI 资源和本地化文本。

## 4. 目录职责

| 路径 | 职责 |
| --- | --- |
| `src/App.axaml` / `src/App.axaml.cs` | 应用资源、启动入口、全局命令、主题/字体/本地化切换、特殊启动模式 |
| `src/Views/` | Avalonia 页面、弹窗、控件和展示组件，通常与同名 ViewModel 对应 |
| `src/ViewModels/` | 主工作流、页面状态、用户操作和异步刷新逻辑 |
| `src/Models/` | Git 领域模型、设置文件、UI 状态、watcher、统计、diff 数据、外部工具配置 |
| `src/Commands/` | Git 命令封装；每个文件通常对应一类 Git 操作或查询 |
| `src/Converters/` | Avalonia binding 转换器 |
| `src/AI/` | AI 服务配置、聊天工具、提交信息生成相关能力 |
| `src/Diagnostics/` | 诊断事件、性能 trace、崩溃记录 |
| `src/Native/` | OS 抽象和 Windows/macOS/Linux 后端 |
| `src/Resources/` | 图标、样式、主题、字体、语法文件、多语言文案 |
| `build/` | CI、发布和平台打包脚本 |
| `depends/AvaloniaEdit` | 内嵌编辑器子模块 |

## 5. 功能地图

### 启动器、工作区和仓库标签页

- `ViewModels.Launcher` / `Views.Launcher`：主窗口和标签页容器。
- `ViewModels.Workspace`：工作区分组和仓库集合。
- `ViewModels.Welcome` / `Views.Welcome`：欢迎页、仓库列表、打开/扫描入口。
- `ViewModels.RepositoryNode`：仓库树节点、分组、未管理仓库节点。

### 仓库工作台

- `ViewModels.Repository` / `Views.Repository` 是打开仓库后的核心上下文。
- 它聚合三块主页面：`Histories`、`WorkingCopy`、`StashesPage`。
- 它维护 branches、remotes、worktrees、tags、submodules、stashes、local changes、history filters 等仓库状态。
- `Models.Watcher` 监听工作区和 Git 目录变化，再驱动 `Repository` 标记 dirty 或刷新。

### 提交历史和提交详情

- `ViewModels.Histories` / `Views.Histories`：提交列表、提交图、筛选、选中提交。
- `Models.CommitGraph`：提交图数据和绘制辅助。
- `ViewModels.CommitDetail`、`RevisionCompare`、`RevisionFiles`：提交详情、版本对比、文件树和文件内容。
- 相关命令集中在 `Commands.QueryCommits*`、`QuerySingleCommit`、`QueryCommitFullMessage`、`QueryRevision*`。

### 工作区变更、暂存和提交

- `ViewModels.WorkingCopy` / `Views.WorkingCopy`：unstaged/staged 列表、筛选、暂存、取消暂存、提交、amend、sign-off、no-verify。
- `ViewModels.CommitMessageEditor`、`CommitMessageToolBox`、`ConventionalCommitMessageBuilder`：提交信息编辑和 Conventional Commit 辅助。
- 相关命令包括 `QueryLocalChanges`、`Add`、`Restore`、`CheckoutIndex`、`Commit`、`Discard`、`SaveChangesAsPatch`。

### Diff 和文件查看

- `ViewModels.DiffContext` 是 diff 内容加载入口。
- 文本 diff 走 `TextDiffContext` / `TextDiffView`，图片 diff 走 `ImageDiffView`，LFS 图片有 `LFSImageDiff`。
- 自定义 diff renderer 由 `Models.CustomDiffRenderer`、`Commands.RenderCustomDiff` 和偏好设置串起来。
- 相关命令包括 `Diff`、`DiffParser`、`DiffTool`、`QueryFileContent`、`QueryRevisionFileObjectSHA`。

### 分支、标签、远端和同步

- 分支：`CreateBranch`、`Checkout`、`RenameBranch`、`DeleteBranch`、`SetUpstream`、`BranchTreeNode`。
- 标签：`CreateTag`、`DeleteTag`、`PushTag`、`TagCollection`。
- 远端：`EditRemote`、`DeleteRemote`、`PruneRemote`、`RemoteProtocolSwitcher`。
- 同步：`Fetch`、`Pull`、`Push`、`FetchInto`、`PushRevision`。
- 对应 Git 命令主要在 `Commands.Branch`、`Remote`、`Fetch`、`Pull`、`Push`、`Tag`。

### 合并、变基、重置和冲突

- 合并与变基：`Merge`、`MergeMultiple`、`Rebase`、`InteractiveRebase`。
- 重置与回退：`Reset`、`ResetWithoutCheckout`、`Revert`、`CheckoutCommit`、`CherryPick`。
- 冲突：`Conflict`、`MergeConflictEditor`、`ConflictHistoryPlan`、`ConflictStageSnapshot`。
- 外部 merge tool 由 `Native.OS`、`Models.ExternalMerger`、`Commands.MergeTool` 串接。

### Stash、Submodule、Worktree、LFS、GitFlow

- Stash：`StashesPage`、`StashChanges`、`ApplyStash`、`DropStash`、`ClearStashes`。
- Submodule：`SubmoduleCollection`、`SubmodulesView`、`AddSubmodule`、`UpdateSubmodules`、`DeleteSubmodule`、`MoveSubmodule`。
- Worktree：`Worktree`、`AddWorktree`、`RemoveWorktree`、`PruneWorktrees`。
- LFS：`LFSFetch`、`LFSPull`、`LFSPush`、`LFSPrune`、`LFSLocks`、`LFSTrackCustomPattern`。
- GitFlow：`InitGitFlow`、`GitFlowStart`、`GitFlowFinish`。

### 搜索、历史、统计和辅助能力

- 文件/目录历史：`FileHistories`、`DirHistories`、`FileHistoryCommandPalette`。
- Blame：`Blame`、`BlameCommandPalette`。
- 命令面板：`RepositoryCommandPalette`、`LauncherPagesCommandPalette`、`CompareCommandPalette` 等。
- 统计：`Statistics`。
- 自定义操作：`CustomAction`、`ExecuteCustomAction`、`ConfigureCustomActionControls`。
- Issue tracker：`Models.IssueTracker`、`Commands.IssueTracker`。

### AI 能力

- `AI.Service` 保存 OpenAI/Azure OpenAI endpoint、API key、model 等配置。
- `AI.Agent`、`AI.ChatTools` 承接具体交互。
- `ViewModels.AIAssistant` 和提交信息工具箱用于生成或辅助提交信息。

### 偏好、配置和运行时数据

- `ViewModels.Preferences` 是全局偏好的单例入口，负责加载设置、准备 Git、shell/terminal、外部 diff/merge 工具和 workspace。
- `Models.RepositorySettings` 是仓库级设置，保存在 Git common dir 的 `sourcegit.settings`。
- `Models.RepositoryUIStates` 保存仓库 UI 状态，如 history 列宽、tag/submodule 展示方式、fetch/push 默认选项等。
- `Native.OS.DataDir` 是应用数据目录，存放偏好、头像、crash log、trace 等运行时数据。

## 6. 关键数据流

### 普通启动

主链路：`App.Main` -> `Native.OS.SetupDataDir` -> `DiagnosticManager.Setup` -> `App.Initialize` 加载资源/主题/语言/字体 -> `ViewModels.Launcher` -> `Views.Launcher`。

### 打开仓库

主链路：`Launcher.TryOpenRepositoryFromPath` -> `Commands.QueryRepositoryRootPath` -> `Preferences.FindOrAddNodeByRepositoryPath` -> 创建 `ViewModels.Repository` -> 显示 `Views.Repository` -> 创建 `Models.Watcher` 监听仓库变化。

### 仓库刷新

主链路：`Models.Watcher` 发现工作区或 Git 目录变化 -> `Repository.Mark*Dirty` 或 `Repository.Refresh*` -> 分别刷新 branches、worktrees、tags、commits、submodules、stashes、working copy。

刷新结果流向：

- `RefreshCommits` 更新 `Histories` 的提交列表、提交图和详情上下文。
- `RefreshWorkingCopyChanges` 更新 `WorkingCopy` 的 unstaged/staged 列表。
- `RefreshStashes` 更新 `StashesPage`。
- `RefreshBranches`、`RefreshTags`、`RefreshSubmodules` 更新仓库侧栏和相关弹窗数据。

### Diff 加载

主链路：选择文件或变更 -> 创建 `ViewModels.DiffContext` -> 根据偏好判断是否启用自定义 renderer。

- 启用自定义 renderer：`Commands.RenderCustomDiff` 生成内容，结果进入 `DiffView.Content`。
- 未启用自定义 renderer：`Commands.Diff` 读取 Git diff，`DiffParser` 解析后进入 `TextDiffContext`、`ImageDiffView` 或 `NoOrEOLChange` 等展示模型。

## 7. 资源与本地化

- 应用资源入口在 `src/App.axaml`。
- 图标和主题通过 `/Resources/Icons.axaml`、`/Resources/Themes.axaml`、`/Resources/Styles.axaml` 合并。
- 多语言文件位于 `src/Resources/Locales/*.axaml`，资源 key 形如 `Text.Xxx`。
- C# 侧通过 `App.Text("Key")` 读取文案；AXAML 侧常见写法是 `{DynamicResource Text.Xxx}`。
- 本地化状态由 `build/scripts/localization-check.js` 维护，并输出到 `TRANSLATION.md`。

## 8. 构建、测试和发布入口

- SDK：`global.json` 指定 .NET SDK `10.0.0`，`rollForward` 为 `latestMajor`。
- 主项目：`src/SourceGit.csproj`，TargetFramework 为 `net10.0`。
- 构建：`dotnet build -c Release`。
- 本地运行：`dotnet run --project src/SourceGit.csproj`。
- 格式检查：`dotnet format --verify-no-changes src/SourceGit.csproj`。
- 本地发布脚本：`powershell -ExecutionPolicy Bypass -File build/scripts/publish-local.win.ps1`。
- 完成代码修改后默认用本地发布脚本打 Windows exe；脚本会自动检查并结束正在运行的 `SourceGit` 进程，再发布到 `publish/SourceGit.exe`。
- 本地发布脚本默认等价于：`dotnet publish src/SourceGit.csproj -c Release -r win-x64 -o publish -p:DisableAOT=true`。
- 如需单文件包：`powershell -ExecutionPolicy Bypass -File build/scripts/publish-local.win.ps1 -Output packages/SourceGit-single-win-x64 -SingleFile -SelfContained`。
- AvaloniaEdit 子模块测试：`dotnet test depends/AvaloniaEdit/test/AvaloniaEdit.Tests/AvaloniaEdit.Tests.csproj`。
- 多语言检查：`node build/scripts/localization-check.js`；CI 会先安装 `fs-extra@11.2.0 path@0.12.7 xml2js@0.6.2`。
- GitHub Actions：`.github/workflows/ci.yml` 调用 build 和 package；format/localization 也有独立 workflow。

## 9. 找功能时的定位方法

- 先按功能名找 `ViewModels/<Feature>.cs` 和 `Views/<Feature>.axaml`。
- 如果功能最终执行 Git 操作，再找 `Commands/<GitArea>.cs` 或 `Commands/Query*.cs`。
- 如果是数据结构、设置、枚举或缓存，通常在 `Models/`。
- 如果是平台差异，优先看 `Native/OS.cs` 和对应平台后端。
- 如果是 UI 文案、主题、图标、字体或语法高亮，优先看 `Resources/`。
- 如果是启动、全局命令、主题/本地化切换或特殊命令行模式，看 `App.axaml.cs`。
- 如果是性能、崩溃或命令执行观测，看 `Diagnostics/` 和 `Commands.Command` 的诊断 span。

## 10. 架构注意点

- `depends/AvaloniaEdit` 是子模块，属于内嵌编辑器依赖，不是主应用普通源码目录。
- `src/bin`、`src/obj` 是构建输出，不属于架构入口。
- 仓库操作不要只看 View；实际 Git 行为通常在 `Commands/`，状态编排在 `ViewModels/`。
- 用户数据兼容性与 `Preferences`、`RepositorySettings`、`RepositoryUIStates` 相关，改字段名或默认值前要理解保存位置和旧数据。
- Release 构建启用 trimming/AOT，涉及反射、序列化、动态加载或新依赖时要注意发布行为。
