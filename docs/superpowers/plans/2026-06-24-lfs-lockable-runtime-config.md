# LFS Lockable Runtime Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为仓库配置增加三态 Git LFS lockable 支持开关，并让 SourceGit 启动的所有 Git 进程统一应用运行时 `-c` 覆盖参数。

**Architecture:** 在仓库设置中保存 `bool?` 三态值；新增 commands 层公共运行时配置 helper，仓库生命周期注册路径到 settings 的映射，Git 进程启动时从内存映射生成 `-c key=value` 参数；所有 Git `ProcessStartInfo` 构建路径复用该 helper，并移除旧的 `core.hooksPath=` 绕行。

**Tech Stack:** C# / .NET, Avalonia XAML, System.Text.Json source generation, Git command-line process launching.

---

### Task 1: 仓库设置模型与运行时配置 helper

**Files:**
- Modify: `src/Models/RepositorySettings.cs`
- Create: `src/Commands/GitRuntimeConfig.cs`
- Modify: `src/ViewModels/Repository.cs`

- [x] **Step 1: 给仓库设置增加三态属性**

在 `RepositorySettings` 中增加：

```csharp
public bool? EnableLFSLockableFiles
{
    get;
    set;
} = false;
```

放在现有 Git 行为设置附近，例如 `AskBeforeAutoUpdatingSubmodules` 后。

- [x] **Step 2: 新增运行时配置 helper**

创建 `src/Commands/GitRuntimeConfig.cs`：

```csharp
using System.Collections.Concurrent;
using System.Text;

namespace SourceGit.Commands
{
    public static class GitRuntimeConfig
    {
        public static void Register(string repo, string gitDir, string gitCommonDir, Models.RepositorySettings settings)
        {
            Register(repo, settings);
            Register(gitDir, settings);
            Register(gitCommonDir, settings);
        }

        public static void Unregister(string repo, string gitDir, string gitCommonDir)
        {
            Unregister(repo);
            Unregister(gitDir);
            Unregister(gitCommonDir);
        }

        public static void Append(StringBuilder builder, string repo)
        {
            var settings = ResolveSettings(repo);
            if (settings?.EnableLFSLockableFiles is { } enabled)
                builder.Append("-c lfs.setlockablereadonly=").Append(enabled ? "true" : "false").Append(' ');
        }

        private static void Register(string path, Models.RepositorySettings settings)
        {
            if (string.IsNullOrWhiteSpace(path) || settings == null)
                return;

            _settingsByPath[Normalize(path)] = settings;
        }

        private static void Unregister(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                _settingsByPath.TryRemove(Normalize(path), out _);
        }

        private static Models.RepositorySettings ResolveSettings(string repo)
        {
            if (string.IsNullOrWhiteSpace(repo))
                return null;

            return _settingsByPath.TryGetValue(Normalize(repo), out var settings) ? settings : null;
        }

        private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

        private static readonly ConcurrentDictionary<string, Models.RepositorySettings> _settingsByPath = new();
    }
}
```

- [x] **Step 3: 注册仓库 settings**

在 `Repository` 构造函数里 `_settings = Models.RepositorySettings.Get(_gitCommonDir);` 后调用：

```csharp
Commands.GitRuntimeConfig.Register(FullPath, GitDir, _gitCommonDir, _settings);
```

在 `Repository.Close()` 里调用：

```csharp
Commands.GitRuntimeConfig.Unregister(FullPath, GitDir, _gitCommonDir);
```

- [x] **Step 4: Review recursion risk**

`GitRuntimeConfig` 不能在 Git 命令启动路径中再执行 Git 命令。它只能查询内存映射；查不到时返回 `null`，保持命令当前行为。

### Task 2: 所有 Git 进程统一追加运行时配置

**Files:**
- Modify: `src/Commands/Command.cs`
- Modify: `src/Commands/MergeConflictBlob.cs`
- Modify: `src/Commands/QueryFileContent.cs`
- Modify: `src/Commands/SaveChangesAsPatch.cs`
- Modify: `src/Commands/SaveRevisionFile.cs`
- Modify: `src/Commands/UpdateIndexInfo.cs`
- Modify: `src/Commands/Checkout.cs`

- [x] **Step 1: 集成 `Command.CreateGitStartInfo`**

在 `Command.CreateGitStartInfo()` 的公共参数 builder 中，保留现有参数并在 credential 前追加：

```csharp
builder.Append("--no-pager -c core.quotepath=off ");
GitRuntimeConfig.Append(builder, WorkingDirectory ?? Context);
builder
    .Append("-c credential.helper=")
    .Append(Native.OS.CredentialHelper)
    .Append(' ');
```

- [x] **Step 2: 集成静态 helper 的 Git 参数**

所有直接设置 `starter.Arguments` 或 `start.Arguments` 的 Git helper 都要改成先创建 `StringBuilder`：

```csharp
var builder = new StringBuilder();
builder.Append("--no-pager ");
GitRuntimeConfig.Append(builder, repo);
builder.Append(args);
starter.Arguments = builder.ToString();
```

对于已有 `--no-pager -c core.quotepath=off` 的路径，保留 `core.quotepath=off` 后调用 `GitRuntimeConfig.Append(...)`。

- [x] **Step 3: 移除 `core.hooksPath=` 绕行**

把 `Checkout.ResetFilesToConflictStateAsync()` 中的：

```csharp
var builder = new StringBuilder("-c core.hooksPath= checkout -m --");
```

改为：

```csharp
var builder = new StringBuilder("checkout -m --");
```

### Task 3: 配置 ViewModel 与 UI

**Files:**
- Modify: `src/ViewModels/RepositoryConfigure.cs`
- Modify: `src/Views/RepositoryConfigure.axaml`

- [x] **Step 1: 暴露三态选项**

在 `RepositoryConfigure` 中增加选项列表和绑定属性：

```csharp
public List<string> LFSLockableOptions { get; } = ["Disabled", "Enabled", "Use Git Config"];

public int LFSLockableOption
{
    get => _repo.Settings.EnableLFSLockableFiles switch
    {
        false => 0,
        true => 1,
        _ => 2,
    };
    set
    {
        var next = value switch
        {
            0 => false,
            1 => true,
            _ => (bool?)null,
        };

        if (_repo.Settings.EnableLFSLockableFiles != next)
        {
            _repo.Settings.EnableLFSLockableFiles = next;
            OnPropertyChanged();
        }
    }
}
```

- [x] **Step 2: 在 Git 页签增加 ComboBox**

把 Git 页签 Grid 的 `RowDefinitions` 增加一行，并在现有 submodule 选项附近加入：

```xml
<TextBlock Grid.Row="12" Grid.Column="0"
           HorizontalAlignment="Right" VerticalAlignment="Center"
           Margin="0,0,8,0"
           Text="{DynamicResource Text.Configure.Git.LFSLockable}"/>
<ComboBox Grid.Row="12" Grid.Column="1"
          Height="28" Padding="8,0"
          VerticalAlignment="Center" HorizontalAlignment="Stretch"
          ItemsSource="{Binding LFSLockableOptions}"
          SelectedIndex="{Binding LFSLockableOption, Mode=TwoWay}"
          ToolTip.Tip="{DynamicResource Text.Configure.Git.LFSLockable.Tip}"/>
```

### Task 4: 本地化

**Files:**
- Modify: `src/Resources/Locales/en_US.axaml`
- Modify: `src/Resources/Locales/zh_CN.axaml`
- Modify: `src/Resources/Locales/zh_TW.axaml`

- [x] **Step 1: 增加英文 key**

在 Git configure key 附近加入：

```xml
<x:String x:Key="Text.Configure.Git.LFSLockable" xml:space="preserve">Git LFS Lockable Files</x:String>
<x:String x:Key="Text.Configure.Git.LFSLockable.Tip" xml:space="preserve">Git LFS checks lockable files in post-checkout hooks. In large repositories this can slow down discard, checkout, and restore operations. Disable this to improve performance; SourceGit-started Git commands will not maintain lockable file read-only states.</x:String>
```

- [x] **Step 2: 增加中文 key**

在 `zh_CN` 与 `zh_TW` 中增加对应翻译，说明性能影响和禁用后的行为差异。

### Task 5: 验证

**Files:**
- No source edits expected.

- [x] **Step 1: grep 验证旧绕行移除**

Run: `rg -n "core\\.hooksPath=|lfs\\.setlockablereadonly" -S src`

Expected: `core.hooksPath=` 无结果；`lfs.setlockablereadonly` 只出现在新的运行时配置 helper 或相关 UI 文案中。

- [x] **Step 2: 构建**

Run: `dotnet build SourceGit.slnx`

Expected: build succeeds with 0 errors.

- [x] **Step 3: 检查工作区**

Run: `git status --short`

Expected: only intended implementation files and spec/plan docs are changed.
