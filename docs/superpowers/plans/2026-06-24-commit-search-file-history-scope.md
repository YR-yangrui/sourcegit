# Commit 搜索与 File History 范围选择 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现提交搜索和 File History 的统一历史范围选择，并让 Path 搜索使用 `--follow` 与 File History 的单文件历史语义保持一致。

**Architecture:** 新增一个 `Models.HistoryQueryScope` 作为 UI 与 Git 命令之间的范围模型，并新增一个专用范围选择控件复用 BranchSelector 的搜索/下拉交互。`QueryCommits` 和 `QueryFileHistory` 接收范围对象构造 Git 参数；`SearchCommitContext` 与 `FileHistories` 负责生成候选、默认选择、切换后刷新结果。

**Tech Stack:** C#、Avalonia XAML、CommunityToolkit.Mvvm、Git 命令封装、SourceGit 现有本地化资源。

---

## 文件结构

- 新建 `src/Models/HistoryQueryScope.cs`：定义范围类型、候选对象、Git 参数映射、显示名、搜索过滤。
- 新建 `src/Views/HistoryQueryScopeSelector.axaml`：范围选择器 UI。
- 新建 `src/Views/HistoryQueryScopeSelector.axaml.cs`：范围选择器属性、过滤、键盘和鼠标交互。
- 修改 `src/Commands/QueryCommits.cs`：把搜索范围从 bool 扩展为 `HistoryQueryScope`，Path 模式加 `--follow`。
- 修改 `src/Commands/QueryFileHistory.cs`：接收 `HistoryQueryScope` 并按范围生成 revision 参数。
- 修改 `src/ViewModels/SearchCommitContext.cs`：生成范围候选，替换 `OnlySearchCurrentBranch`，范围变化后重新搜索。
- 修改 `src/Views/Repository.axaml`：把“Current Branch” CheckBox 换成范围选择器。
- 修改 `src/ViewModels/FileHistories.cs`：接收 Repository、生成范围候选、显示历史起点、切换范围刷新列表。
- 修改 `src/Views/FileHistories.axaml`：顶部增加“查看范围”和“历史起点”两行。
- 修改 File History 入口：`src/ViewModels/FileHistoryCommandPalette.cs`、`src/ViewModels/RepositoryCommandPalette.cs`、`src/Views/CommitDetail.axaml.cs`、`src/Views/RevisionFileTreeView.axaml.cs`、`src/Views/WorkingCopy.axaml.cs`、`src/Views/SubmodulesView.axaml.cs`。
- 修改 `src/Resources/Locales/en_US.axaml`、`src/Resources/Locales/zh_CN.axaml`、其他 locale 文件只补英文 fallback 文案，避免缺资源。

---

### Task 1: 新增历史查询范围模型

**Files:**
- Create: `src/Models/HistoryQueryScope.cs`

- [ ] **Step 1: 编写范围模型**

新增文件包含以下类型和方法：

```csharp
namespace SourceGit.Models
{
    public enum HistoryQueryScopeKind
    {
        AllBranches,
        CurrentBranch,
        Head,
        Revision,
        Branch,
    }

    public class HistoryQueryScope
    {
        public HistoryQueryScopeKind Kind { get; init; }
        public Branch Branch { get; init; }
        public string Revision { get; init; }
        public string Name { get; init; }
        public string IconKey { get; init; }

        public bool IsAllBranches => Kind == HistoryQueryScopeKind.AllBranches;
        public bool IsCurrentHead => Kind is HistoryQueryScopeKind.CurrentBranch or HistoryQueryScopeKind.Head;
        public bool IsBranch => Kind == HistoryQueryScopeKind.Branch && Branch != null;
        public string RevisionArg => Kind switch
        {
            HistoryQueryScopeKind.Revision => Revision,
            HistoryQueryScopeKind.Branch => Branch?.FullName,
            _ => string.Empty,
        };

        public static HistoryQueryScope AllBranches(string name) => new() { Kind = HistoryQueryScopeKind.AllBranches, Name = name, IconKey = "Icons.Branches" };
        public static HistoryQueryScope CurrentBranch(string name, Branch branch) => new() { Kind = HistoryQueryScopeKind.CurrentBranch, Branch = branch, Name = name, IconKey = "Icons.Branch" };
        public static HistoryQueryScope Head(string name) => new() { Kind = HistoryQueryScopeKind.Head, Name = name, IconKey = "Icons.Head" };
        public static HistoryQueryScope RevisionScope(string sha) => new() { Kind = HistoryQueryScopeKind.Revision, Revision = sha, Name = sha.Length > 10 ? sha[..10] : sha, IconKey = "Icons.Commit" };
        public static HistoryQueryScope BranchScope(Branch branch) => new() { Kind = HistoryQueryScopeKind.Branch, Branch = branch, Name = branch.FriendlyName, IconKey = "Icons.Branch" };
    }
}
```

- [ ] **Step 2: 构建并修正图标键**

运行：

```powershell
dotnet build src/SourceGit.csproj
```

若 `Icons.Branches`、`Icons.Head` 或 `Icons.Commit` 不存在，改用已有的 `Icons.Branch`、`Icons.Histories`、`Icons.Commit` 中存在的键，并重新构建。

Expected: 编译通过或只暴露缺失图标资源错误。

---

### Task 2: 新增范围选择器控件

**Files:**
- Create: `src/Views/HistoryQueryScopeSelector.axaml`
- Create: `src/Views/HistoryQueryScopeSelector.axaml.cs`

- [ ] **Step 1: 实现 code-behind 属性与过滤**

实现 `Scopes`、`VisibleScopes`、`SelectedScope`、`IsDropDownOpened`、`SearchFilter` 五个属性。过滤逻辑按 `scope.Name.Contains(SearchFilter, OrdinalIgnoreCase)`，保持旧选择仍可见时不改变选择，否则自动选中第一项。

```csharp
public static readonly StyledProperty<List<Models.HistoryQueryScope>> ScopesProperty =
    AvaloniaProperty.Register<HistoryQueryScopeSelector, List<Models.HistoryQueryScope>>(nameof(Scopes));

public static readonly DirectProperty<HistoryQueryScopeSelector, Models.HistoryQueryScope> SelectedScopeProperty =
    AvaloniaProperty.RegisterDirect<HistoryQueryScopeSelector, Models.HistoryQueryScope>(
        nameof(SelectedScope), o => o.SelectedScope, (o, v) => o.SelectedScope = v);
```

- [ ] **Step 2: 实现 XAML 模板**

模板结构与 `BranchSelector` 保持一致：外层 Border、当前选中项、Popup、搜索框、ListBox。ItemTemplate 显示一个图标和 `Name`，分支范围不额外显示 new branch 标记。

```xml
<DataTemplate DataType="m:HistoryQueryScope">
  <StackPanel Orientation="Horizontal" Background="Transparent" Height="28" PointerPressed="OnDropDownItemPointerPressed">
    <Path Width="14" Height="14" Fill="{DynamicResource Brush.FG1}" Data="{StaticResource Icons.Branch}"/>
    <TextBlock Margin="8,0,0,0" VerticalAlignment="Center" Text="{Binding Name}"/>
  </StackPanel>
</DataTemplate>
```

- [ ] **Step 3: 构建验证控件**

运行：

```powershell
dotnet build src/SourceGit.csproj
```

Expected: XAML 编译通过。

---

### Task 3: 更新 Git 查询命令

**Files:**
- Modify: `src/Commands/QueryCommits.cs`
- Modify: `src/Commands/QueryFileHistory.cs`

- [ ] **Step 1: 扩展 QueryCommits 构造函数**

保留原有 `QueryCommits(string repo, string limits, bool markMerged = true)`。把搜索构造函数改成：

```csharp
public QueryCommits(string repo, string filter, Models.CommitSearchMethod method, Models.HistoryQueryScope scope)
```

范围参数生成规则：

```csharp
if (scope?.IsAllBranches == true)
    builder.Append("--branches --remotes ");
else if (!string.IsNullOrEmpty(scope?.RevisionArg))
    builder.Append(scope.RevisionArg.Quoted()).Append(' ');
```

Path 模式使用：

```csharp
builder.Append("--follow -- ").Append(filter.Quoted());
```

- [ ] **Step 2: 更新 QueryFileHistory 构造函数**

构造函数改成：

```csharp
public QueryFileHistory(string repo, string path, Models.HistoryQueryScope scope)
```

在 `--follow --name-status` 后追加范围：

```csharp
if (scope?.IsAllBranches == true)
    builder.Append("--branches --remotes ");
else if (!string.IsNullOrEmpty(scope?.RevisionArg))
    builder.Append(scope.RevisionArg.Quoted()).Append(' ');
```

再追加 `-- <path>`。

- [ ] **Step 3: 编译定位调用点**

运行：

```powershell
dotnet build src/SourceGit.csproj
```

Expected: 编译失败只出现在旧构造函数调用点，记录并在后续任务更新。

---

### Task 4: 更新提交搜索 ViewModel 与 UI

**Files:**
- Modify: `src/ViewModels/SearchCommitContext.cs`
- Modify: `src/Views/Repository.axaml`
- Modify: `src/Resources/Locales/en_US.axaml`
- Modify: `src/Resources/Locales/zh_CN.axaml`

- [ ] **Step 1: 在 SearchCommitContext 中增加范围属性**

新增：

```csharp
public List<Models.HistoryQueryScope> Scopes { get; private set; }
public Models.HistoryQueryScope SelectedScope { get; set; }
public bool IsScopeSelectorVisible => _method != (int)Models.CommitSearchMethod.BySHA;
```

构造函数从 `_repo.Branches` 和 `_repo.CurrentBranch` 生成候选，默认 `AllBranches`。

- [ ] **Step 2: 替换 OnlySearchCurrentBranch 逻辑**

删除或停止使用 `OnlySearchCurrentBranch`。`StartSearch` 中按 `SelectedScope` 调用新的 `QueryCommits`。`IsMerged` 标记逻辑：

- `CurrentBranch` 或 `Head`：结果都标记 merged。
- 其他范围：继续用 `QueryCurrentBranchCommitHashes` 判断是否已合入当前 HEAD。

- [ ] **Step 3: 更新 Repository.axaml**

把原 CheckBox 替换为：

```xml
<v:HistoryQueryScopeSelector Height="24"
                             MinWidth="180"
                             Margin="4,0,0,0"
                             Scopes="{Binding SearchCommitContext.Scopes}"
                             SelectedScope="{Binding SearchCommitContext.SelectedScope, Mode=TwoWay}"
                             IsVisible="{Binding SearchCommitContext.IsScopeSelectorVisible}"/>
```

- [ ] **Step 4: 本地化文案**

新增资源：

```xml
<x:String x:Key="Text.HistoryScope.AllBranches" xml:space="preserve">All branches</x:String>
<x:String x:Key="Text.HistoryScope.CurrentBranch" xml:space="preserve">Current branch</x:String>
<x:String x:Key="Text.HistoryScope.Head" xml:space="preserve">HEAD</x:String>
```

中文：

```xml
<x:String x:Key="Text.HistoryScope.AllBranches" xml:space="preserve">所有分支</x:String>
<x:String x:Key="Text.HistoryScope.CurrentBranch" xml:space="preserve">当前分支</x:String>
<x:String x:Key="Text.HistoryScope.Head" xml:space="preserve">HEAD</x:String>
```

- [ ] **Step 5: 构建验证提交搜索**

运行：

```powershell
dotnet build src/SourceGit.csproj
```

Expected: 提交搜索相关编译通过。

---

### Task 5: 更新 File History 范围选择与入口

**Files:**
- Modify: `src/ViewModels/FileHistories.cs`
- Modify: `src/Views/FileHistories.axaml`
- Modify: `src/ViewModels/FileHistoryCommandPalette.cs`
- Modify: `src/ViewModels/RepositoryCommandPalette.cs`
- Modify: `src/Views/CommitDetail.axaml.cs`
- Modify: `src/Views/RevisionFileTreeView.axaml.cs`
- Modify: `src/Views/WorkingCopy.axaml.cs`
- Modify: `src/Views/SubmodulesView.axaml.cs`
- Modify: locale files under `src/Resources/Locales`

- [ ] **Step 1: FileHistories 改为接收 Repository**

主构造函数改成：

```csharp
public FileHistories(Repository repo, string file, string commit = null)
```

保存 `_repo = repo`、`_repoPath = repo.FullPath`、`_file = file`、`_openedRevision = commit`。

- [ ] **Step 2: 添加范围候选与默认值**

新增属性：

```csharp
public List<Models.HistoryQueryScope> Scopes { get; private set; }
public Models.HistoryQueryScope SelectedScope { get; set; }
public string StartingPointDescription { get; private set; }
```

默认规则：

- 有 `commit`：选中 `RevisionScope(commit)`。
- 无 `commit` 且 `repo.CurrentBranch != null`：选中 `CurrentBranch`。
- 无 `commit` 且 `repo.CurrentBranch == null`：选中 `Head`。

- [ ] **Step 3: 生成历史起点文本**

范围变化时调用 `RefreshStartingPointAsync`：

- `AllBranches`：`App.Text("FileHistory.StartingPoint.AllBranches")`
- `CurrentBranch`：查询 `SelectedScope.Branch.Head` 的 commit，显示 `branch.Name - subject`
- `Head`：查询 `HEAD`，显示 `HEAD - <shortsha> - subject`
- `Revision`：查询 revision，显示 `<shortsha> - subject`
- `Branch`：查询 branch.Head，显示 `branch.FriendlyName - subject`

- [ ] **Step 4: 刷新历史列表**

范围变化调用 `RefreshRevisions`，使用：

```csharp
var revisions = await new Commands.QueryFileHistory(_repoPath, _file, _selectedScope).GetResultAsync();
```

刷新前设置 `IsLoading = true`，清空 `SelectedRevisions` 和 `ViewContent`；刷新后设置 `Revisions` 和 `IsLoading = false`。

- [ ] **Step 5: 更新 FileHistories.axaml 顶部**

在原 Info 行上方或替换原 Info 行，添加：

```xml
<Grid RowDefinitions="32,24" ColumnDefinitions="Auto,*">
  <TextBlock Text="{DynamicResource Text.FileHistory.ViewIn}"/>
  <v:HistoryQueryScopeSelector Scopes="{Binding Scopes}" SelectedScope="{Binding SelectedScope, Mode=TwoWay}"/>
  <TextBlock Text="{DynamicResource Text.FileHistory.StartingPoint}"/>
  <TextBlock Text="{Binding StartingPointDescription}" TextTrimming="CharacterEllipsis"/>
</Grid>
```

- [ ] **Step 6: 更新所有入口**

把 `new ViewModels.FileHistories(repo.FullPath, path, commit)` 改成 `new ViewModels.FileHistories(repo, path, commit)`。命令面板 `FileHistoryCommandPalette` 从保存 repo path 改为保存 `Repository repo`。

- [ ] **Step 7: 本地化文案**

英文：

```xml
<x:String x:Key="Text.FileHistory.ViewIn" xml:space="preserve">View In</x:String>
<x:String x:Key="Text.FileHistory.StartingPoint" xml:space="preserve">Starting Point</x:String>
<x:String x:Key="Text.FileHistory.StartingPoint.AllBranches" xml:space="preserve">All branches - showing file history from every local and remote branch</x:String>
```

中文：

```xml
<x:String x:Key="Text.FileHistory.ViewIn" xml:space="preserve">查看范围</x:String>
<x:String x:Key="Text.FileHistory.StartingPoint" xml:space="preserve">历史起点</x:String>
<x:String x:Key="Text.FileHistory.StartingPoint.AllBranches" xml:space="preserve">所有分支 - 显示所有本地和远端分支中的文件历史</x:String>
```

- [ ] **Step 8: 构建验证 File History**

运行：

```powershell
dotnet build src/SourceGit.csproj
```

Expected: 编译通过。

---

### Task 6: 验证与收尾

**Files:**
- Modify if needed: files touched by prior tasks

- [ ] **Step 1: 静态检查所有调用点**

运行：

```powershell
rg -n "new ViewModels\\.FileHistories|new FileHistories\\(|QueryFileHistory\\(|QueryCommits\\([^\\n]*CommitSearchMethod|OnlySearchCurrentBranch|InCurrentBranch" src
```

Expected: 不再有旧 FileHistories 构造、旧 QueryFileHistory 构造、旧搜索 bool 范围逻辑。

- [ ] **Step 2: 构建**

运行：

```powershell
dotnet build src/SourceGit.csproj
```

Expected: Build succeeded。

- [ ] **Step 3: 用真实仓库手动验证 Git 参数语义**

在 `F:\work\nslg` 或 `G:\work\nslg-1.24.0` 运行：

```powershell
$p='Program/trunk/Client/LuaScripts/Main.lua'
@(git log --follow --format='%H' -- $p).Count
@(git log --format='%H' -- $p).Count
```

Expected: `--follow` 的数量大于或等于无 `--follow`，用于确认 Path 搜索修复的目标场景。

- [ ] **Step 4: 最终 review**

请求只读子代理 review：

```text
Review the implementation against docs/superpowers/specs/2026-06-24-commit-search-file-history-scope-design.md.
Check whether Path search uses --follow only for path mode, all scope selectors have correct defaults, File History displays View In and Starting Point correctly, and there are no obvious C# or Avalonia binding regressions. Do not edit files.
```

Expected: review 无阻断问题；若有问题，修复后重新构建。
