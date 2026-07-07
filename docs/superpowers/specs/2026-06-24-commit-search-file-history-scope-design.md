# Commit 搜索与文件历史范围选择设计

## 目标

让提交搜索和 File History 的历史范围语义更清晰、一致，并修正按路径搜索时默认 `git log -- <path>` 在合并历史中漏掉部分文件修改提交的问题。

## 背景

当前提交搜索面板支持按 SHA、作者、提交者、提交消息、路径、变更内容搜索。除 SHA 外，其余模式目前只有“当前分支”和“所有分支/远端分支”的二选一逻辑，其中“所有分支”由 `--branches --remotes` 实现。

当前按路径搜索使用 `git log ... -- <path>`。在存在 merge commit 的仓库中，Git 会对带 pathspec 的历史做 history simplification，导致部分侧分支上确实修改过该路径的提交不会出现在结果里。File History 使用 `--follow --name-status -- <path>`，能看到更多单文件历史。因此用户会看到“按 Path 搜索只有少量记录，但 File History 有更多记录”的不一致。

当前 File History 有多个入口：

- 从 Working Copy、命令面板等入口打开时，没有传入 commit，实际从 `HEAD` 开始查询。
- 从某个 commit/revision 的文件列表或变更列表打开时，会传入该 commit 的 SHA，实际从该 commit 开始查询。

File History 目前没有显示这个起点，也不支持用户在窗口内切换查询范围。

## 需求

- Path 搜索和 File History 这种单文件路径历史查询需要使用 `--follow`。
- Author、Committer、Message、Content 搜索不使用 `--follow`，因为 `git log --follow` 要求恰好一个 pathspec。
- Author、Committer、Message、Path、Content 五种可选择范围的搜索模式都要支持：
  - 所有分支
  - 当前分支，若当前处于 detached HEAD，则显示为 HEAD
  - 指定本地分支
  - 指定远端分支
- 提交搜索面板的范围选择默认选中“所有分支”。
- File History 要显示当前历史查询是从哪个提交或范围开始的。
- File History 要支持在窗口内切换查看范围，候选包括“所有分支”、当前分支或 HEAD、创建窗口时传入的 SHA、本地分支、远端分支。
- 文案和设计文档使用中文描述；代码中的资源键仍按现有项目约定命名。

## 设计

### 统一范围模型

新增一个轻量的历史查询范围模型，用于提交搜索和 File History 复用。范围类型包括：

- `AllBranches`：所有本地和远端分支。
- `CurrentBranch`：当前 HEAD 所在分支。
- `Head`：detached HEAD 时的当前 HEAD。
- `Revision`：指定提交 SHA，主要用于 File History 创建入口传入的 commit。
- `Branch`：指定本地或远端分支。

范围模型需要能提供：

- UI 显示名称。
- Git revision 参数。
- 是否为聚合范围。
- 是否代表当前 HEAD。

Git 参数映射：

- `AllBranches` 使用 `--branches --remotes`。
- `CurrentBranch` 不额外传 revision，沿用 `HEAD`。
- `Head` 不额外传 revision，沿用 detached `HEAD`。
- `Revision` 传入对应 SHA。
- `Branch` 传入 branch 的 `FullName` 或明确可解析的 ref 名称。

### 提交搜索

SearchCommitContext 将把现有 `OnlySearchCurrentBranch` 替换为一个范围选择属性。搜索方法为 SHA 时不显示范围选择；Author、Committer、Message、Path、Content 时显示范围选择。

范围选择器候选顺序：

1. 所有分支
2. 当前分支；如果 detached HEAD，则为 HEAD
3. 本地分支列表
4. 远端分支列表

默认选中“所有分支”。

Path 搜索构造命令时，在确定输入是单一路径后使用 `--follow -- <path>`。因为 Path 模式本身只有一个路径输入，所以满足 Git 对 `--follow` 的限制。其他搜索模式不添加 `--follow`。

Content 搜索继续使用 `-G<filter>`。本次设计不强制加入 `-m`，避免改变 merge commit 展示数量和去重语义；如果后续要解决 merge resolution 中引入内容搜不到的问题，可单独设计。

### File History 顶部信息

File History 窗口顶部增加两行：

```text
View In:          [ 范围选择器 ]
Starting Point:   <当前范围对应的起点说明>
```

建议中文文案：

- `View In`：`查看范围`
- `Starting Point`：`历史起点`

`Starting Point` 中文推荐使用“历史起点”，因为它比“开始于”更适合做字段标签，也能表达当前 File History 的查询起始引用。

范围选择器候选顺序：

1. 所有分支
2. 当前分支；如果 detached HEAD，则为 HEAD
3. 创建 File History 时传入的 SHA，显示 10 位短 SHA；仅在入口传入 commit 时出现
4. 本地分支列表
5. 远端分支列表

默认选中规则：

- 入口传入 commit SHA 时，默认选中该 SHA，保留现有“从点击来源提交往前看”的语义。
- 入口未传入 commit SHA 且 HEAD 在分支上时，默认选中“当前分支”。
- 入口未传入 commit SHA 且处于 detached HEAD 时，默认选中“HEAD”。

`Starting Point` 显示规则：

- 选中具体分支：`<branch name> - <commit message>`
- 选中当前分支：`<branch name> - <commit message>`
- 选中 detached HEAD：`HEAD - <10位sha> - <commit message>`
- 选中入口传入的 SHA：`<10位sha> - <commit message>`
- 选中所有分支：`所有分支 - 显示所有本地和远端分支中的文件历史`

如果 commit message 为空，则显示短 SHA 后不追加空的分隔符。长文本在 UI 中按现有 TextBlock ellipsis 规则截断。

### File History 查询

File History 的查询命令仍使用 `--follow --name-status -- <path>`。

范围参数映射：

- `AllBranches`：`git log --follow --name-status --branches --remotes -- <path>`
- `CurrentBranch`：`git log --follow --name-status -- <path>`
- `Head`：`git log --follow --name-status -- <path>`
- `Revision`：`git log --follow --name-status <sha> -- <path>`
- `Branch`：`git log --follow --name-status <branch-ref> -- <path>`

切换范围时重新查询 File History，并清空当前选中的 revision 和右侧详情内容。查询期间显示现有 loading 状态。

### 分支选择交互

现有 `BranchSelector` 只处理 `Models.Branch`，不能直接表示“所有分支”“当前分支”“HEAD”“入口 SHA”这些特殊项。新增一个用于历史范围的选择控件或数据模板，而不是把特殊项塞进 `Models.Branch`。

新选择器应复用现有 BranchSelector 的交互习惯：

- 点击打开下拉。
- 顶部支持搜索。
- 候选列表可键盘上下移动和 Enter 选择。
- 本地/远端分支显示现有分支图标和友好名称。
- 特殊项使用合适的图标与文本区分，但不新增复杂分组。

## 数据流

提交搜索：

1. Repository 持有分支列表和当前分支状态。
2. SearchCommitContext 基于 Repository 构造范围候选，默认选中“所有分支”。
3. 用户切换范围或搜索条件后，重新执行 QueryCommits。
4. QueryCommits 根据范围构造 revision 参数，根据搜索模式构造 filter 参数。
5. Path 搜索使用 `--follow -- <path>`，其他模式不使用 `--follow`。

File History：

1. 打开入口传入 repo、file、可选 commit SHA。
2. FileHistories 初始化范围候选，并根据入口选择默认范围。
3. FileHistories 根据当前范围查询起点说明。
4. FileHistories 执行 QueryFileHistory，并把范围映射为对应 git 参数。
5. 用户切换范围时重复步骤 3 和 4。

## 错误处理

- 如果分支列表为空，范围选择器仍保留“所有分支”和 HEAD/当前分支选项。
- 如果入口传入的 SHA 已无法解析，候选中仍显示短 SHA，但查询失败时展示空历史并保留 loading 结束状态；错误通知沿用现有 Command 机制。
- 如果分支被删除或刷新后当前选中范围失效，File History 和提交搜索回退到“所有分支”。
- `--follow` 只在单文件路径查询中使用，避免触发 `fatal: --follow requires exactly one pathspec`。

## 测试方案

- 用包含 merge 历史的仓库验证 Path 搜索：
  - 当前实现 `git log -- <path>` 只返回少量提交。
  - 新实现 `git log --follow -- <path>` 与 File History 的单文件结果保持一致。
- 验证提交搜索范围：
  - 所有分支使用 `--branches --remotes`。
  - 当前分支/HEAD 不追加 `--branches --remotes`。
  - 指定本地分支和远端分支只搜索对应 ref。
- 验证 detached HEAD：
  - 搜索面板和 File History 显示 HEAD 选项，不显示 Current branch。
  - `Starting Point` 显示 `HEAD - <10位sha> - <commit message>`。
- 验证 File History 入口：
  - 从 Working Copy 打开时默认选中当前分支或 HEAD。
  - 从 commit 文件列表打开时默认选中入口 SHA。
  - 切换到所有分支时显示 `所有分支 - 显示所有本地和远端分支中的文件历史`。
  - 切换分支后重新加载列表并清空右侧详情。

## 非目标

- 不改变 SHA 搜索的行为。
- 不为 Author、Committer、Message、Content 搜索添加 `--follow`。
- 不在本次设计中解决 Content 搜索对 merge resolution diff 需要 `-m` 的问题。
- 不重构 Repository 的分支刷新机制。
