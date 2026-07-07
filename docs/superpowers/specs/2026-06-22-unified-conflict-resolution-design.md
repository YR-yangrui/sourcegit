# 统一冲突处理设计

## 目标

统一 merge、rebase、cherry-pick、revert、stash、patch 冲突的处理逻辑，同时让不同操作类型的冲突预览保持清晰、不误导用户。

## 背景

SourceGit 现在的 merge conflict 预览比 rebase、revert、cherry-pick 的冲突视图更完整。merge 冲突可以显示冲突文件两侧的提交历史，在冲突会话中缓存这些历史，并且可以通过 index stage 直接构造 external merge 所需的临时文件。其他进行中的操作虽然在 Git 里同样通过 unmerged index stages 表示冲突文件，但展示逻辑和 `CONTINUE` 处理逻辑不一致。

期望的行为是：所有冲突共享同一套底层解决机制；但只有在历史信息足够有意义时，才展示更丰富的两侧历史面板。

## 需求

- 只有 merge 和 rebase 冲突显示双栏冲突历史面板。
- cherry-pick、revert、stash、patch 以及其他冲突来源继续使用简单的 `MINE` / `THEIRS` 卡片预览。
- merge 历史保持现有的 `merge-base HEAD MERGE_HEAD` 行为：
  - `MINE`：显示 `base..HEAD` 中修改过冲突文件的提交。
  - `THEIRS`：显示 `base..MERGE_HEAD` 中修改过冲突文件的提交。
- rebase 历史：
  - 计算 `base = merge-base HEAD <stopped-sha>`。
  - `MINE`：显示 `base..HEAD` 中修改过冲突文件的提交。
  - `THEIRS`：显示当前正在 replay 的提交，也就是 `<stopped-sha>`。
- merge、rebase、cherry-pick、revert、stash、patch 冲突的 external merge、`USE MINE`、`USE THEIRS` 都通过 index stage 处理。
- stage 1 表示 `BASE`，stage 2 表示 `MINE`，stage 3 表示 `THEIRS`。
- `USE MINE` 和 `USE THEIRS` 保留删除类冲突的特殊处理。
- `USE MINE` 或 `USE THEIRS` 完成后，stage 已解决路径，并触发 working copy status refresh。
- `CONTINUE` 使用 SourceGit commit message 输入框中用户实际看到的内容。
- `CONTINUE` 不打开 SourceGit 的 rebase message editor，也不打开 Git 配置的 editor。
- commit message 中的冲突 detail 必须按输入框中显示的内容保留。

## 设计

在 `InProgressContext` 之上引入一个轻量的冲突操作视图契约。

每种 in-progress context 需要暴露足够的元数据，用于：

- 预览界面的两侧标签，以及简单 `MINE` / `THEIRS` 卡片内容。
- 冲突预览是否使用历史面板。
- 历史面板需要查询的 range 或精确 commit。
- external merge 临时文件的两侧名称。
- continue 命令的 editor 行为。
- continue 前应写入的 message 文件路径。

现有 merge 专用的历史缓存应改为 operation-aware 的冲突历史缓存。缓存仍然通过 in-progress session 和 unmerged index snapshot hash 来决定是否复用数据，但不再直接依赖 `MergeInProgress`，而是接收一个 history plan。如果没有可用的 history plan，缓存重置，预览界面退回简单卡片布局。

## 预览行为

`MergeInProgress` 的 history plan：

- session identity：当前分支 HEAD、当前分支名、`MERGE_HEAD`。
- base：`merge-base HEAD MERGE_HEAD`。
- mine range：`base..HEAD`。
- theirs range：`base..MERGE_HEAD`。
- 标题：左侧显示当前分支，右侧显示正在 merge 的 source。

`RebaseInProgress` 的 history plan：

- session identity：当前 `HEAD`、`rebase-merge/onto`、`rebase-merge/stopped-sha`、unmerged index snapshot hash。
- base：`merge-base HEAD <stopped-sha>`。
- mine range：`base..HEAD`。
- theirs data：如果 `<stopped-sha>` 修改过该文件，则显示这个单独的 file-version entry。
- 标题：左侧显示当前 `HEAD` 侧，右侧显示当前正在 replay 的 commit。

`CherryPickInProgress`、`RevertInProgress`、stash、patch 或未知冲突来源使用现有简单卡片布局。这些 context 仍然提供 `MINE` 和 `THEIRS` 的标签与内容，但不请求历史。

## 冲突操作

创建或复用一条共享的 stage-based 操作路径：

- `USE MINE` 对普通文件冲突 checkout stage 2。
- `USE THEIRS` 对普通文件冲突 checkout stage 3。
- 删除类冲突保留现有 delete-and-stage 处理。
- 写入选择的一侧后，SourceGit 通过 `git add` stage 对应路径，并标记 working copy dirty，让 status 刷新。

External merge 使用同一套事实来源：

- 通过 `checkout-index` 查询 unmerged index stages。
- 将 stage 1、stage 2、stage 3 写入临时文件。
- 使用这些临时文件以及 working copy path 作为 `$MERGED` 调用配置好的 external merge tool。
- external tool 退出后，检查冲突标记是否仍然存在，并更新当前选中冲突的显示状态。

这样 merge、rebase、cherry-pick、revert、stash、patch 都通过 Git 已经维护的 index stages 来解决冲突，行为保持一致。

## Continue 行为

所有 in-progress operation 的 `CONTINUE` 都变成所见即所得：

- 运行 continue 命令前，SourceGit 将当前 `WorkingCopy.CommitMessage` 写入对应操作的 message 文件。
- 写入的文本必须匹配 SourceGit commit message 输入框。SourceGit 可以把平台换行统一成 `\n`，但不能插入空行、删除空行、删除 comment 行或改写 conflict detail。
- merge、cherry-pick、revert 在 `.git/MERGE_MSG` 存在时使用它。
- rebase 在 `.git/rebase-merge/message` 存在时使用它。
- continue 命令使用 `EditorType.None`，也就是配置 `core.editor=true`。
- continue 命令传入 `commit.cleanup=verbatim` 和 `commit.status=false`，与现有 merge continue 行为一致。

验收规则很简单：用户在 SourceGit commit message 输入框里看到什么，点击 `CONTINUE` 后生成的 commit message 就必须是什么。

## 验证

- 实现后构建项目。
- 手动验证 merge 和 rebase 冲突：只有这两类显示历史面板。
- 手动验证 cherry-pick、revert、stash apply、patch apply 冲突：这些仍然显示简单卡片预览。
- 对每种冲突来源验证 `USE MINE`、`USE THEIRS`、external merge 都通过 index stage 处理，并在操作后刷新 status。
- 验证 `CONTINUE` 不打开 editor，并且生成的 commit message 与 SourceGit 输入框内容完全一致。

## 不在范围内

- 不重新设计冲突预览的视觉样式。
- 不为 cherry-pick、revert、stash、patch 冲突增加历史面板。
- 不改变内置文本 merge editor 的行为，只复用同一套两侧元数据。
- 不引入 Git index stages 之外的新冲突解决语义。
