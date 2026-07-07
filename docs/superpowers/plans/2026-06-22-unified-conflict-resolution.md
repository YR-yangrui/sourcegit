# 统一冲突处理 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 统一 merge/rebase/cherry-pick/revert/stash/patch 冲突的预览、external merge、`USE MINE`/`USE THEIRS` 和 `CONTINUE` 行为。

**Architecture:** 在 `InProgressContext` 上补一层冲突操作元数据，让 UI、历史缓存、external merge 文件命名和 continue message 写入都从同一个上下文读取。保留现有 stage-based `checkout-index` 解决路径，把历史缓存从 merge-only 扩展为接受 history plan。`CONTINUE` 单独使用 line-ending-only 的 message 写入，避免复用普通 commit formatter 造成内容改写。

**Tech Stack:** C# / .NET 10、Avalonia、Git CLI、现有 SourceGit ViewModel/Command 架构。

---

## 文件结构

- Modify: `src/ViewModels/InProgressContexts.cs`
  - 为 in-progress 操作提供冲突两侧展示、历史查询计划、external merge 文件名、continue message 文件和 editor 行为。
- Create: `src/ViewModels/ConflictHistoryPlan.cs`
  - 保存 merge/rebase 历史面板需要的 session key、标题、merge-base 输入和 history range 构造规则。
- Modify: `src/ViewModels/MergeConflictFileHistoryCache.cs`
  - 保留现有类名，改为接收 `ConflictHistoryPlan`，支持 merge 和 rebase 的历史查询。
- Modify: `src/ViewModels/Conflict.cs`
  - 把 `IsMergeInProgress` 改为 `UseHistoryPanel`，用 context 元数据决定简单卡片或历史面板。
- Modify: `src/Views/Conflict.axaml`
  - 绑定 `UseHistoryPanel`，让只有 merge/rebase 显示历史面板。
- Modify: `src/ViewModels/MergeConflictEditor.cs`
  - 内置文本 merge editor 的两侧信息改为复用 context 元数据。
- Modify: `src/ViewModels/WorkingCopy.cs`
  - 历史缓存 ensure、external merge 文件名和 `CONTINUE` message 写入改为统一上下文逻辑。
- Modify: `src/ViewModels/Repository.cs`
  - 关闭仓库时保存 in-progress commit message 使用同一套 WYSIWYG 写入。
- Modify: `src/Models/CommitMessageFormatter.cs`
  - 增加只统一换行、不改写内容的 helper，普通 commit 继续使用原有 formatter。

---

### Task 1: 添加冲突历史计划和 in-progress 元数据

**Files:**
- Create: `src/ViewModels/ConflictHistoryPlan.cs`
- Modify: `src/ViewModels/InProgressContexts.cs`

- [ ] **Step 1: 新增 `ConflictHistoryPlan`**

Create `src/ViewModels/ConflictHistoryPlan.cs`:

```csharp
namespace SourceGit.ViewModels
{
    public class ConflictHistoryPlan
    {
        public string SessionSeed { get; init; } = string.Empty;
        public string MineTitle { get; init; } = "MINE";
        public string TheirsTitle { get; init; } = "THEIRS";
        public string MergeBaseLeft { get; init; } = "HEAD";
        public string MergeBaseRight { get; init; } = string.Empty;
        public string MineTip { get; init; } = "HEAD";
        public string TheirsTip { get; init; } = string.Empty;
        public bool TheirsIsSingleCommit { get; init; } = false;

        public bool IsValid =>
            !string.IsNullOrEmpty(SessionSeed) &&
            !string.IsNullOrEmpty(MergeBaseLeft) &&
            !string.IsNullOrEmpty(MergeBaseRight) &&
            !string.IsNullOrEmpty(MineTip) &&
            !string.IsNullOrEmpty(TheirsTip);

        public string BuildMineRange(string mergeBase)
        {
            return $"{mergeBase}..{MineTip}";
        }

        public string BuildTheirsRange(string mergeBase)
        {
            return TheirsIsSingleCommit ? $"{TheirsTip}^!" : $"{mergeBase}..{TheirsTip}";
        }
    }
}
```

- [ ] **Step 2: 给 `InProgressContext` 增加默认冲突元数据接口**

In `src/ViewModels/InProgressContexts.cs`, add these members to `InProgressContext` after `Name`:

```csharp
public virtual ConflictHistoryPlan CreateConflictHistoryPlan(Repository repo)
{
    return null;
}

public virtual (object Mine, object Theirs) GetConflictSides(Models.Commit head)
{
    return (head, (object)"Stash or Patch");
}

public virtual (string Mine, string Theirs) GetExternalMergeFileSideNames()
{
    return ("HEAD", string.Empty);
}

public virtual string GetContinueMessageFile(Repository repo)
{
    var file = Path.Combine(repo.GitDir, "MERGE_MSG");
    return File.Exists(file) ? file : string.Empty;
}
```

- [ ] **Step 3: 更新 `CherryPickInProgress` 冲突元数据**

Add these overrides to `CherryPickInProgress`:

```csharp
public override (object Mine, object Theirs) GetConflictSides(Models.Commit head)
{
    return (head, Head);
}

public override (string Mine, string Theirs) GetExternalMergeFileSideNames()
{
    return ("HEAD", string.IsNullOrEmpty(HeadName) ? Head.SHA : HeadName);
}
```

- [ ] **Step 4: 更新 `RebaseInProgress` 冲突元数据**

Add these overrides to `RebaseInProgress`:

```csharp
public override ConflictHistoryPlan CreateConflictHistoryPlan(Repository repo)
{
    var stopped = StoppedAt?.SHA ?? string.Empty;
    if (string.IsNullOrEmpty(stopped))
        return null;

    var head = repo.CurrentBranch?.Head ?? "HEAD";
    return new ConflictHistoryPlan
    {
        SessionSeed = $"{head}\0{Onto?.SHA ?? string.Empty}\0{stopped}\0{HeadName}\0",
        MineTitle = "MINE - current HEAD",
        TheirsTitle = $"THEIRS - rebasing: {StoppedAt.GetFriendlyName()}",
        MergeBaseLeft = "HEAD",
        MergeBaseRight = stopped,
        MineTip = "HEAD",
        TheirsTip = stopped,
        TheirsIsSingleCommit = true,
    };
}

public override (object Mine, object Theirs) GetConflictSides(Models.Commit head)
{
    return (Onto, StoppedAt);
}

public override (string Mine, string Theirs) GetExternalMergeFileSideNames()
{
    return (
        string.IsNullOrEmpty(BaseName) ? "HEAD" : BaseName,
        StoppedAt?.GetFriendlyName() ?? "REBASE_HEAD");
}

public override string GetContinueMessageFile(Repository repo)
{
    var file = Path.Combine(repo.GitDir, "rebase-merge", "message");
    return File.Exists(file) ? file : string.Empty;
}
```

- [ ] **Step 5: 更新 `RevertInProgress` 冲突元数据**

Add these overrides to `RevertInProgress`:

```csharp
public override (object Mine, object Theirs) GetConflictSides(Models.Commit head)
{
    return (head, Head);
}

public override (string Mine, string Theirs) GetExternalMergeFileSideNames()
{
    return ("HEAD", Head?.GetFriendlyName() ?? "REVERT_HEAD");
}
```

- [ ] **Step 6: 更新 `MergeInProgress` 元数据**

Add these overrides to `MergeInProgress`:

```csharp
public override ConflictHistoryPlan CreateConflictHistoryPlan(Repository repo)
{
    var head = repo.CurrentBranch?.Head ?? string.Empty;
    var current = string.IsNullOrEmpty(Current) ? "HEAD" : Current;
    var source = Source?.SHA ?? string.Empty;
    if (string.IsNullOrEmpty(source))
        return null;

    return new ConflictHistoryPlan
    {
        SessionSeed = $"{head}\0{current}\0{source}\0",
        MineTitle = $"MINE - current branch: {current}",
        TheirsTitle = $"THEIRS - merging: {SourceName}",
        MergeBaseLeft = "HEAD",
        MergeBaseRight = "MERGE_HEAD",
        MineTip = "HEAD",
        TheirsTip = "MERGE_HEAD",
    };
}

public override (object Mine, object Theirs) GetConflictSides(Models.Commit head)
{
    return (head, Source);
}

public override (string Mine, string Theirs) GetExternalMergeFileSideNames()
{
    return (
        string.IsNullOrEmpty(Current) ? "HEAD" : Current,
        string.IsNullOrEmpty(SourceName) ? "MERGE_HEAD" : SourceName);
}
```

- [ ] **Step 7: 构建验证 Task 1**

Run:

```powershell
dotnet build SourceGit.slnx
```

Expected: build succeeds, or fails only for a pre-existing local dependency/environment issue that must be recorded before continuing.

- [ ] **Step 8: Commit Task 1**

Before committing, follow `.agents/skills/codex-commit/SKILL.md`: request read-only subagent review for this task's diff.

Commit message:

```text
feat: Add conflict operation metadata(添加冲突操作元数据)
```

---

### Task 2: 将历史缓存改为支持 merge 和 rebase

**Files:**
- Modify: `src/ViewModels/MergeConflictFileHistoryCache.cs`
- Modify: `src/ViewModels/WorkingCopy.cs`

- [ ] **Step 1: 修改 `MergeConflictFileHistoryCache.Ensure` 签名**

In `src/ViewModels/MergeConflictFileHistoryCache.cs`, replace:

```csharp
public void Ensure(Repository repo, MergeInProgress merge)
```

with:

```csharp
public void Ensure(Repository repo, ConflictHistoryPlan plan)
```

At the start of the method, add:

```csharp
if (plan == null || !plan.IsValid)
{
    Reset();
    return;
}

var sessionSeed = plan.SessionSeed;
```

Remove the old local `sessionSeed = BuildSessionSeed(...)` line.

- [ ] **Step 2: 让 cache 使用 plan 计算 merge-base 和 ranges**

Inside the background task in `Ensure`, replace the merge-specific block:

```csharp
mergeBase = await new Commands.MergeBase(repo.FullPath, "HEAD", "MERGE_HEAD")
    .GetResultAsync()
    .ConfigureAwait(false);
contextKey = BuildContextKey(sessionSeed, snapshot.Hash);

missing.AddRange(snapshot.Paths);

if (!string.IsNullOrEmpty(mergeBase) && missing.Count > 0)
{
    var mineTask = new Commands.QueryFileHistories(repo.FullPath, $"{mergeBase}..HEAD", missing)
        .GetResultAsync();
    var theirsTask = new Commands.QueryFileHistories(repo.FullPath, $"{mergeBase}..MERGE_HEAD", missing)
        .GetResultAsync();

    await Task.WhenAll(mineTask, theirsTask).ConfigureAwait(false);
    mine = mineTask.Result;
    theirs = theirsTask.Result;
}
```

with:

```csharp
mergeBase = await new Commands.MergeBase(repo.FullPath, plan.MergeBaseLeft, plan.MergeBaseRight)
    .GetResultAsync()
    .ConfigureAwait(false);
contextKey = BuildContextKey(sessionSeed, snapshot.Hash);

missing.AddRange(snapshot.Paths);

if (!string.IsNullOrEmpty(mergeBase) && missing.Count > 0)
{
    var mineTask = new Commands.QueryFileHistories(repo.FullPath, plan.BuildMineRange(mergeBase), missing)
        .GetResultAsync();
    var theirsTask = new Commands.QueryFileHistories(repo.FullPath, plan.BuildTheirsRange(mergeBase), missing)
        .GetResultAsync();

    await Task.WhenAll(mineTask, theirsTask).ConfigureAwait(false);
    mine = mineTask.Result;
    theirs = theirsTask.Result;
}
```

Delete `BuildSessionSeed` if it is no longer used.

- [ ] **Step 3: 更新 `WorkingCopy.EnsureMergeConflictHistoryCache`**

In `src/ViewModels/WorkingCopy.cs`, replace `EnsureMergeConflictHistoryCache()` with:

```csharp
private void EnsureMergeConflictHistoryCache()
{
    var plan = _inProgressContext?.CreateConflictHistoryPlan(_repo);
    if (plan != null)
        MergeConflictHistories.Ensure(_repo, plan);
    else
        MergeConflictHistories.Reset();
}
```

- [ ] **Step 4: 构建验证 Task 2**

Run:

```powershell
dotnet build SourceGit.slnx
```

Expected: build succeeds.

- [ ] **Step 5: Commit Task 2**

Request read-only subagent review before commit.

Commit message:

```text
feat: Cache conflict histories for rebase(缓存 rebase 冲突历史)
```

---

### Task 3: 更新冲突预览 UI 只让 merge/rebase 显示历史面板

**Files:**
- Modify: `src/ViewModels/Conflict.cs`
- Modify: `src/Views/Conflict.axaml`
- Modify: `src/ViewModels/MergeConflictEditor.cs`

- [ ] **Step 1: 将 `Conflict.IsMergeInProgress` 改为 `UseHistoryPanel`**

In `src/ViewModels/Conflict.cs`, replace the property:

```csharp
public bool IsMergeInProgress
{
    get => _isMergeInProgress;
    private set => SetProperty(ref _isMergeInProgress, value);
}
```

with:

```csharp
public bool UseHistoryPanel
{
    get => _useHistoryPanel;
    private set => SetProperty(ref _useHistoryPanel, value);
}
```

Update empty-history properties:

```csharp
public bool IsMineHistoryEmpty
{
    get => UseHistoryPanel && !IsLoadingHistories && _mineHistories.Count == 0;
}

public bool IsTheirsHistoryEmpty
{
    get => UseHistoryPanel && !IsLoadingHistories && _theirsHistories.Count == 0;
}
```

Replace the backing field:

```csharp
private bool _useHistoryPanel = false;
```

- [ ] **Step 2: 简化 `Conflict` constructor 的 context 分支**

In `Conflict` constructor, after `CanMerge` calculation, replace the current `if (wc.InProgressContext is MergeInProgress...) else ...` block with:

```csharp
if (CanMerge)
    IsResolved = new Commands.IsConflictResolved(repo.FullPath, change).GetResult();

_head = new Commands.QuerySingleCommit(repo.FullPath, "HEAD").GetResult() ?? new Models.Commit() { SHA = "HEAD" };
var context = wc.InProgressContext;
(Mine, Theirs) = context?.GetConflictSides(_head) ?? (_head, (object)"Stash or Patch");

var historyPlan = context?.CreateConflictHistoryPlan(repo);
if (historyPlan != null)
{
    UseHistoryPanel = true;
    MineHistoryTitle = historyPlan.MineTitle;
    TheirsHistoryTitle = historyPlan.TheirsTitle;

    _historyCache = wc.MergeConflictHistories;
    _historyCache.PropertyChanged += OnHistoryCachePropertyChanged;
    RefreshHistoriesFromCache();
}

StartResolveCheck();
```

- [ ] **Step 3: 更新 XAML 绑定**

In `src/Views/Conflict.axaml`, replace both bindings:

```xml
IsVisible="{Binding IsMergeInProgress}"
```

and:

```xml
IsVisible="{Binding !IsMergeInProgress}"
```

with:

```xml
IsVisible="{Binding UseHistoryPanel}"
```

and:

```xml
IsVisible="{Binding !UseHistoryPanel}"
```

- [ ] **Step 4: 更新内置 merge editor 的两侧信息**

In `src/ViewModels/MergeConflictEditor.cs`, replace:

```csharp
(Mine, Theirs) = repo.InProgressContext switch
{
    CherryPickInProgress cherryPick => (head, cherryPick.Head),
    RebaseInProgress rebase => (rebase.Onto, rebase.StoppedAt),
    RevertInProgress revert => (head, revert.Head),
    MergeInProgress merge => (head, merge.Source),
    _ => (head, (object)"Stash or Patch"),
};
```

with:

```csharp
(Mine, Theirs) = repo.InProgressContext?.GetConflictSides(head) ?? (head, (object)"Stash or Patch");
```

- [ ] **Step 5: 搜索确认没有旧绑定残留**

Run:

```powershell
rg -n "IsMergeInProgress" src
```

Expected: no matches.

- [ ] **Step 6: 构建验证 Task 3**

Run:

```powershell
dotnet build SourceGit.slnx
```

Expected: build succeeds.

- [ ] **Step 7: Commit Task 3**

Request read-only subagent review before commit.

Commit message:

```text
feat: Show conflict histories only for merge and rebase(仅为 merge 和 rebase 显示冲突历史)
```

---

### Task 4: 统一 external merge 文件名和确认 stage-based 操作路径

**Files:**
- Modify: `src/ViewModels/WorkingCopy.cs`

- [ ] **Step 1: 让 external merge 文件名来自 `InProgressContext`**

In `src/ViewModels/WorkingCopy.cs`, replace `GetExternalMergeFileSideNames()` with:

```csharp
private (string Mine, string Their) GetExternalMergeFileSideNames()
{
    return _inProgressContext?.GetExternalMergeFileSideNames() ?? ("HEAD", string.Empty);
}
```

- [ ] **Step 2: 确认 `USE MINE` 仍使用 stage 2**

In `UseMineAsync`, keep the checkout call exactly stage 2:

```csharp
var succ = await new Commands.CheckoutIndex(_repo.FullPath).Use(log).CheckoutStageAsync(2, files);
```

If this line has changed during earlier edits, restore it.

- [ ] **Step 3: 确认 `USE THEIRS` 仍使用 stage 3**

In `UseTheirsAsync`, keep the checkout call exactly stage 3:

```csharp
var succ = await new Commands.CheckoutIndex(_repo.FullPath).Use(log).CheckoutStageAsync(3, files);
```

If this line has changed during earlier edits, restore it.

- [ ] **Step 4: 确认 external merge 仍通过 `MergeConflictBlob.CreateMergeFilesAsync` 使用 stages**

In `OpenExternalMergeToolForChangeAsync`, keep this flow:

```csharp
var files = await Commands.MergeConflictBlob.CreateMergeFilesAsync(
    _repo.FullPath,
    change.Path,
    mineName,
    theirName,
    hasBaseStage).ConfigureAwait(false);
```

No new non-stage file extraction path should be added.

- [ ] **Step 5: 构建验证 Task 4**

Run:

```powershell
dotnet build SourceGit.slnx
```

Expected: build succeeds.

- [ ] **Step 6: Commit Task 4**

Request read-only subagent review before commit.

Commit message:

```text
refactor: Share external merge conflict side names(复用 external merge 冲突侧名称)
```

---

### Task 5: 实现 `CONTINUE` commit message 所见即所得

**Files:**
- Modify: `src/ViewModels/InProgressContexts.cs`
- Modify: `src/Models/CommitMessageFormatter.cs`
- Modify: `src/ViewModels/WorkingCopy.cs`
- Modify: `src/ViewModels/Repository.cs`

- [ ] **Step 1: 将所有 in-progress continue 命令切换为 editor-free**

In `src/ViewModels/InProgressContexts.cs`, change `CherryPickInProgress` `_continueCmd` to:

```csharp
_continueCmd = new Commands.Command
{
    WorkingDirectory = repo.FullPath,
    Context = repo.FullPath,
    Editor = Commands.Command.EditorType.None,
    Args = "-c commit.cleanup=verbatim -c commit.status=false cherry-pick --continue",
};
```

Change `RebaseInProgress` `_continueCmd` to:

```csharp
_continueCmd = new Commands.Command
{
    WorkingDirectory = repo.FullPath,
    Context = repo.FullPath,
    Editor = Commands.Command.EditorType.None,
    Args = "-c commit.cleanup=verbatim -c commit.status=false rebase --continue",
};
```

Change `RevertInProgress` `_continueCmd` to:

```csharp
_continueCmd = new Commands.Command
{
    WorkingDirectory = repo.FullPath,
    Context = repo.FullPath,
    Editor = Commands.Command.EditorType.None,
    Args = "-c commit.cleanup=verbatim -c commit.status=false revert --continue",
};
```

Keep `MergeInProgress` on `EditorType.None` and `-c commit.cleanup=verbatim -c commit.status=false merge --continue`.

- [ ] **Step 2: 增加 line-ending-only formatter**

In `src/Models/CommitMessageFormatter.cs`, add this method before `NormalizeForGit`:

```csharp
public static string NormalizeLineEndingsForGit(string message)
{
    return string.IsNullOrEmpty(message) ? message : message.ReplaceLineEndings("\n");
}
```

Do not change `NormalizeForGit`; normal commits still need its subject/body formatting behavior.

- [ ] **Step 3: 在 `WorkingCopy` 增加同步保存 helper**

In `src/ViewModels/WorkingCopy.cs`, add this public method near `Close()`:

```csharp
public void SaveInProgressCommitMessage()
{
    if (_inProgressContext == null)
        return;

    var file = _inProgressContext.GetContinueMessageFile(_repo);
    if (string.IsNullOrEmpty(file))
        return;

    File.WriteAllText(file, Models.CommitMessageFormatter.NormalizeLineEndingsForGit(_commitMessage ?? string.Empty));
}
```

- [ ] **Step 4: `ContinueMergeAsync` 使用 WYSIWYG 保存**

In `ContinueMergeAsync`, replace:

```csharp
var mergeMsgFile = Path.Combine(_repo.GitDir, "MERGE_MSG");
if (File.Exists(mergeMsgFile))
    await File.WriteAllTextAsync(mergeMsgFile, Models.CommitMessageFormatter.NormalizeForGit(_commitMessage ?? string.Empty));
```

with:

```csharp
SaveInProgressCommitMessage();
```

- [ ] **Step 5: 仓库关闭时使用同一保存逻辑**

In `src/ViewModels/Repository.cs`, replace:

```csharp
var commitMessage = _workingCopy.CommitMessage;
if (!string.IsNullOrEmpty(commitMessage) && _workingCopy.InProgressContext != null)
    File.WriteAllText(Path.Combine(GitDir, "MERGE_MSG"), Models.CommitMessageFormatter.NormalizeForGit(commitMessage));
```

with:

```csharp
var commitMessage = _workingCopy.CommitMessage;
_workingCopy.SaveInProgressCommitMessage();
```

- [ ] **Step 6: 搜索确认 continue 不再使用 rebase editor**

Run:

```powershell
rg -n "rebase --continue|cherry-pick --continue|revert --continue|merge --continue|EditorType.RebaseEditor|NormalizeForGit\\(_commitMessage" src/ViewModels src/Models
```

Expected:

- `rebase --continue`, `cherry-pick --continue`, `revert --continue`, and `merge --continue` all use `EditorType.None`.
- `EditorType.RebaseEditor` remains only for interactive rebase startup/editor paths, not conflict continue.
- `NormalizeForGit(_commitMessage` no longer appears in in-progress continue handling.

- [ ] **Step 7: 构建验证 Task 5**

Run:

```powershell
dotnet build SourceGit.slnx
```

Expected: build succeeds.

- [ ] **Step 8: Commit Task 5**

Request read-only subagent review before commit.

Commit message:

```text
fix: Preserve in-progress continue messages(保留进行中操作的 continue 信息)
```

---

### Task 6: 最终验证

**Files:**
- No source changes expected unless verification finds a bug.

- [ ] **Step 1: 全量构建**

Run:

```powershell
dotnet build SourceGit.slnx
```

Expected: build succeeds.

- [ ] **Step 2: 静态搜索验证预览分流**

Run:

```powershell
rg -n "UseHistoryPanel|CreateConflictHistoryPlan|IsMergeInProgress|MergeConflictHistories.Ensure" src/ViewModels src/Views
```

Expected:

- `UseHistoryPanel` is used by `Conflict.cs` and `Conflict.axaml`.
- `CreateConflictHistoryPlan` exists on `InProgressContext`, `MergeInProgress`, and `RebaseInProgress`.
- No `IsMergeInProgress` matches.
- `MergeConflictHistories.Ensure` is called with a `ConflictHistoryPlan`.

- [ ] **Step 3: 静态搜索验证 stage-based 操作**

Run:

```powershell
rg -n "CheckoutStageAsync\\(2|CheckoutStageAsync\\(3|CreateMergeFilesAsync|checkout-index -f --stage" src/ViewModels src/Commands
```

Expected:

- `USE MINE` uses `CheckoutStageAsync(2, files)`.
- `USE THEIRS` uses `CheckoutStageAsync(3, files)`.
- external merge still uses `CreateMergeFilesAsync`.
- `MergeConflictBlob` still uses `checkout-index -f --stage`.

- [ ] **Step 4: 静态搜索验证 WYSIWYG continue**

Run:

```powershell
rg -n "NormalizeLineEndingsForGit|NormalizeForGit\\(_commitMessage|commit.cleanup=verbatim|commit.status=false|EditorType.None" src/ViewModels src/Models
```

Expected:

- `NormalizeLineEndingsForGit` is used for in-progress message persistence.
- `NormalizeForGit(_commitMessage` does not appear.
- all continue commands use `commit.cleanup=verbatim`, `commit.status=false`, and `EditorType.None`.

- [ ] **Step 5: 手动场景验证**

Use temporary repositories outside this working tree for manual checks. For each conflict scenario:

```powershell
git status --short
```

Expected after resolving with `USE MINE` or `USE THEIRS`: unmerged entries disappear and the resolved path appears staged or modified according to Git's normal status output.

Expected after resolving with external merge: the external tool receives stage-derived `BASE`, `MINE`, and `THEIRS` files, the working copy path is used as the merged output, the selected conflict display updates after the tool exits, and status refreshes after the result is staged or marked resolved.

For merge, rebase, cherry-pick, and revert only, expected after `CONTINUE`: no editor opens, and:

```powershell
git log -1 --format=%B
```

matches the text that was visible in SourceGit's commit message input.

Manual scenarios:

- merge conflict: history panel visible; verify `USE MINE`, `USE THEIRS`, external merge, status refresh, and `CONTINUE`.
- rebase conflict: history panel visible; right side shows only current replayed commit for that file; verify `USE MINE`, `USE THEIRS`, external merge, status refresh, and `CONTINUE`.
- cherry-pick conflict: simple `MINE` / `THEIRS` card visible; verify `USE MINE`, `USE THEIRS`, external merge, status refresh, and `CONTINUE`.
- revert conflict: simple `MINE` / `THEIRS` card visible; verify `USE MINE`, `USE THEIRS`, external merge, status refresh, and `CONTINUE`.
- stash apply conflict: simple `MINE` / `THEIRS` card visible; verify `USE MINE`, `USE THEIRS`, external merge, and status refresh.
- patch apply conflict: simple `MINE` / `THEIRS` card visible; verify `USE MINE`, `USE THEIRS`, external merge, and status refresh.

- [ ] **Step 6: Final implementation commit if verification fixes were needed**

If Task 6 required source fixes, request read-only subagent review before committing.

Commit message:

```text
fix: Complete unified conflict verification fixes(完成统一冲突验证修复)
```
