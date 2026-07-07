# GitLab Self Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build GitLab release automation and Windows self-update support for SourceGit while keeping macOS/Linux update actions as browser downloads.

**Architecture:** Add typed update metadata and a GitLab-backed update checker in SourceGit, add a Windows-only `SourceGit.Updater` project adapted from `F:\work\hygames\Tools\GameUpdater`, and add GitLab packaging/release scripts that publish rolling channel releases plus versioned packages. The client downloads and verifies a manifest, Windows downloads a matching self-update zip and launches the updater from a user-writable staging directory, and macOS/Linux open the release page.

**Tech Stack:** C#/.NET 10, Avalonia, Windows Win32 P/Invoke updater, PowerShell/Bash packaging scripts, GitLab CI/CD, GitLab Releases API, GitLab Generic Package Registry.

---

## File Structure

- Modify `src/SourceGit.csproj`: add SourceGit update assembly metadata with CI-overridable MSBuild properties.
- Create `src/Models/BuildInfo.cs`: read SourceGit update metadata from assembly attributes.
- Create `src/Models/UpdateChannel.cs`: define Stable/Nightly channel ids, release tags, and display keys.
- Create `src/Models/UpdateManifest.cs`: JSON DTOs for `sourcegit-update.json`.
- Create `src/Models/GitLabRelease.cs`: JSON DTOs for GitLab release API responses.
- Create `src/Models/UpdateChecker.cs`: fetch rolling release JSON, fetch manifest, compare with current build.
- Create `src/Models/UpdateInstaller.cs`: Windows zip download, SHA-256 verification, updater extraction and launch.
- Modify `src/Models/SelfUpdate.cs`: replace old GitHub Pages version model with `UpdateAvailable`, `AlreadyUpToDate`, and `SelfUpdateFailed` result models.
- Modify `src/App.JsonCodeGen.cs`: register new JSON DTOs.
- Modify `src/App.axaml.cs`: route check-for-update through `UpdateChecker`.
- Modify `src/ViewModels/Preferences.cs`: persist update channel and channel-scoped ignored versions.
- Modify `src/Views/Preferences.axaml`: add update channel selector beside update startup preference.
- Modify `src/Views/SelfUpdate.axaml` and `src/Views/SelfUpdate.axaml.cs`: show channel/version and add Windows install action.
- Modify `src/Resources/Locales/en_US.axaml` and `src/Resources/Locales/zh_CN.axaml`: add update channel/install strings.
- Create `src/SourceGit.Updater/SourceGit.Updater.csproj`: Windows-only updater executable project.
- Create `src/SourceGit.Updater/Program.cs`: updater entry point.
- Create `src/SourceGit.Updater/SourceGitUpdater.cs`: SourceGit-specific directory replacement flow adapted from `GameUpdater.cs`.
- Create `src/SourceGit.Updater/FileUtils.cs`, `src/SourceGit.Updater/UpdaterUI.cs`, `src/SourceGit.Updater/Win32UI.cs`: adapted helper/UI files.
- Create `src/SourceGit.Updater/app.manifest`: `requireAdministrator` updater manifest.
- Modify `SourceGit.slnx`: include the updater project and GitLab CI files.
- Create `build/scripts/package.gitlab.win.ps1`: create updater-compatible Windows zip layout.
- Create `build/scripts/gitlab/package-manifest.ps1`: generate manifest and SHA-256 checksums.
- Create `build/scripts/gitlab/release-assets.sh`: upload generic packages and upsert release links.
- Create `build/scripts/gitlab/cleanup-nightly.sh`: keep newest 30 nightly package versions.
- Create `.gitlab-ci.yml`: scheduled nightly and manual stable pipeline.

---

### Task 1: Build Metadata And Update DTOs

**Files:**
- Modify: `src/SourceGit.csproj`
- Create: `src/Models/BuildInfo.cs`
- Create: `src/Models/UpdateChannel.cs`
- Create: `src/Models/UpdateManifest.cs`
- Create: `src/Models/GitLabRelease.cs`
- Modify: `src/App.JsonCodeGen.cs`

- [ ] **Step 1: Add CI-overridable metadata to `src/SourceGit.csproj`**

Add these properties inside the first `<PropertyGroup>` after `<Version>`:

```xml
    <SourceGitUpdateChannel Condition="'$(SourceGitUpdateChannel)' == ''">local</SourceGitUpdateChannel>
    <SourceGitUpdateVersion Condition="'$(SourceGitUpdateVersion)' == ''">$(Version)</SourceGitUpdateVersion>
    <SourceGitBaseVersion Condition="'$(SourceGitBaseVersion)' == ''">$(Version)</SourceGitBaseVersion>
    <SourceGitPipelineIid Condition="'$(SourceGitPipelineIid)' == ''"></SourceGitPipelineIid>
    <SourceGitCommit Condition="'$(SourceGitCommit)' == ''"></SourceGitCommit>
    <SourceGitBuildDate Condition="'$(SourceGitBuildDate)' == ''">$([System.DateTime]::UtcNow.ToString('o'))</SourceGitBuildDate>
```

Add these items inside the existing `<ItemGroup>` that already contains `AssemblyMetadata Include="BuildDate"`:

```xml
    <AssemblyMetadata Include="SourceGitChannel" Value="$(SourceGitUpdateChannel)" />
    <AssemblyMetadata Include="SourceGitVersion" Value="$(SourceGitUpdateVersion)" />
    <AssemblyMetadata Include="SourceGitBaseVersion" Value="$(SourceGitBaseVersion)" />
    <AssemblyMetadata Include="SourceGitPipelineIid" Value="$(SourceGitPipelineIid)" />
    <AssemblyMetadata Include="SourceGitCommit" Value="$(SourceGitCommit)" />
    <AssemblyMetadata Include="SourceGitBuildDate" Value="$(SourceGitBuildDate)" />
```

- [ ] **Step 2: Create `src/Models/BuildInfo.cs`**

```csharp
using System;
using System.Linq;
using System.Reflection;

namespace SourceGit.Models
{
    public static class BuildInfo
    {
        public static string Channel { get; } = GetMetadata("SourceGitChannel", "local");
        public static string Version { get; } = GetMetadata("SourceGitVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0");
        public static string BaseVersion { get; } = GetMetadata("SourceGitBaseVersion", Version);
        public static string PipelineIid { get; } = GetMetadata("SourceGitPipelineIid", string.Empty);
        public static string Commit { get; } = GetMetadata("SourceGitCommit", string.Empty);
        public static DateTime BuildDateUtc { get; } = ParseDate(GetMetadata("SourceGitBuildDate", string.Empty));

        public static string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "unknown" : Version;

        private static string GetMetadata(string key, string fallback)
        {
            var attr = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(x => x.Key.Equals(key, StringComparison.Ordinal));
            return string.IsNullOrWhiteSpace(attr?.Value) ? fallback : attr.Value;
        }

        private static DateTime ParseDate(string value)
        {
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.UnixEpoch;
        }
    }
}
```

- [ ] **Step 3: Create `src/Models/UpdateChannel.cs`**

```csharp
using System;

namespace SourceGit.Models
{
    public enum UpdateChannel
    {
        Stable = 0,
        Nightly = 1,
    }

    public static class UpdateChannels
    {
        public static string GetReleaseTag(UpdateChannel channel)
        {
            return channel == UpdateChannel.Nightly ? "nightly-release" : "stable-release";
        }

        public static string GetDisplayText(UpdateChannel channel)
        {
            return channel == UpdateChannel.Nightly ? App.Text("UpdateChannel.Nightly") : App.Text("UpdateChannel.Stable");
        }

        public static UpdateChannel Normalize(int value)
        {
            return Enum.IsDefined(typeof(UpdateChannel), value) ? (UpdateChannel)value : UpdateChannel.Stable;
        }
    }
}
```

- [ ] **Step 4: Create `src/Models/UpdateManifest.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SourceGit.Models
{
    public class UpdateManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("baseVersion")]
        public string BaseVersion { get; set; } = string.Empty;

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        [JsonPropertyName("commit")]
        public string Commit { get; set; } = string.Empty;

        [JsonPropertyName("pipelineId")]
        public string PipelineId { get; set; } = string.Empty;

        [JsonPropertyName("pipelineIid")]
        public string PipelineIid { get; set; } = string.Empty;

        [JsonPropertyName("publishedAt")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("releaseNotes")]
        public string ReleaseNotes { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<UpdateAsset> Assets { get; set; } = [];

        public UpdateAsset? FindAsset(string runtime, string kind)
        {
            return Assets.Find(x =>
                x.Runtime.Equals(runtime, StringComparison.OrdinalIgnoreCase) &&
                x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        }
    }

    public class UpdateAsset
    {
        [JsonPropertyName("runtime")]
        public string Runtime { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 5: Create `src/Models/GitLabRelease.cs`**

```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SourceGit.Models
{
    public class GitLabRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public GitLabReleaseAssets Assets { get; set; } = new();
    }

    public class GitLabReleaseAssets
    {
        [JsonPropertyName("links")]
        public List<GitLabReleaseAssetLink> Links { get; set; } = [];
    }

    public class GitLabReleaseAssetLink
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("direct_asset_url")]
        public string DirectAssetUrl { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 6: Register JSON DTOs in `src/App.JsonCodeGen.cs`**

Add these attributes next to the existing `Models.Version` registration. Task 2 removes the legacy `Models.Version` registration when the old update model is replaced.

```csharp
    [JsonSerializable(typeof(Models.UpdateManifest))]
    [JsonSerializable(typeof(Models.GitLabRelease))]
```

- [ ] **Step 7: Build-check Task 1**

Run:

```powershell
dotnet build SourceGit.slnx --no-restore
```

Expected: either a successful compile, or only pre-existing restore/submodule errors. Any compile error in new model files must be fixed before moving on.

- [ ] **Step 8: Commit Task 1**

```powershell
git add src/SourceGit.csproj src/Models/BuildInfo.cs src/Models/UpdateChannel.cs src/Models/UpdateManifest.cs src/Models/GitLabRelease.cs src/App.JsonCodeGen.cs
git commit -m "feat: Add update metadata models(新增更新元数据模型)"
```

---

### Task 2: GitLab Update Checker And Client Results

**Files:**
- Create: `src/Models/UpdateChecker.cs`
- Create: `src/Models/UpdateInstaller.cs`
- Modify: `src/Models/SelfUpdate.cs`
- Modify: `src/App.JsonCodeGen.cs`
- Modify: `src/App.axaml.cs`

- [ ] **Step 1: Replace `src/Models/SelfUpdate.cs` result models**

Keep `AlreadyUpToDate` and `SelfUpdateFailed`, and add `UpdateAvailable`:

```csharp
using System;
namespace SourceGit.Models
{
    public class UpdateAvailable
    {
        public UpdateManifest Manifest { get; init; } = new();
        public UpdateAsset? Asset { get; init; }
        public UpdateChannel Channel { get; init; } = UpdateChannel.Stable;
        public string ReleasePageUrl { get; init; } = string.Empty;

        public string ChannelName => UpdateChannels.GetDisplayText(Channel);
        public string TagName => Manifest.Version;
        public string Body => Manifest.ReleaseNotes;
        public string CurrentVersionStr => BuildInfo.DisplayVersion;
        public string ReleaseDateStr => DateTimeFormat.Format(Manifest.PublishedAt, true);
        public bool CanInstall => OperatingSystem.IsWindows() && Asset != null;
    }

    public class AlreadyUpToDate;

    public class SelfUpdateFailed
    {
        public string Reason { get; private set; }

        public SelfUpdateFailed(Exception e)
        {
            Reason = e.InnerException?.Message ?? e.Message;
        }
    }
}
```

- [ ] **Step 2: Create `src/Models/UpdateChecker.cs`**

```csharp
using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public static class UpdateChecker
    {
        private const string PROJECT_API = "http://gitlab.zjhuayu.top/api/v4/projects/all%2Fsourcegit";
        private const string RELEASE_PAGE = "http://gitlab.zjhuayu.top/all/sourcegit/-/releases";

        public static async Task<object> CheckAsync(UpdateChannel channel)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var releaseTag = UpdateChannels.GetReleaseTag(channel);
            var releaseJson = await client.GetStringAsync($"{PROJECT_API}/releases/{Uri.EscapeDataString(releaseTag)}").ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(releaseJson, JsonCodeGen.Default.GitLabRelease);
            if (release == null)
                throw new InvalidOperationException("Invalid GitLab release response.");

            var manifestLink = release.Assets.Links.Find(x => x.Name.Equals("sourcegit-update.json", StringComparison.OrdinalIgnoreCase));
            var manifestUrl = manifestLink?.DirectAssetUrl;
            if (string.IsNullOrWhiteSpace(manifestUrl))
                manifestUrl = manifestLink?.Url;
            if (string.IsNullOrWhiteSpace(manifestUrl))
                throw new InvalidOperationException("GitLab release does not contain sourcegit-update.json.");

            var manifestJson = await client.GetStringAsync(manifestUrl).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(manifestJson, JsonCodeGen.Default.UpdateManifest);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
                throw new InvalidOperationException("Invalid update manifest.");

            if (!IsNewer(manifest, channel))
                return new AlreadyUpToDate();

            return new UpdateAvailable
            {
                Manifest = manifest,
                Channel = channel,
                Asset = OperatingSystem.IsWindows() ? manifest.FindAsset(GetWindowsRuntime(), "self-update-zip") : null,
                ReleasePageUrl = $"{RELEASE_PAGE}/{releaseTag}",
            };
        }

        private static bool IsNewer(UpdateManifest manifest, UpdateChannel channel)
        {
            if (!manifest.Channel.Equals(channel == UpdateChannel.Nightly ? "nightly" : "stable", StringComparison.OrdinalIgnoreCase))
                return false;

            var currentBase = ParseVersion(BuildInfo.BaseVersion);
            var remoteBase = ParseVersion(manifest.BaseVersion);
            var baseComparison = remoteBase.CompareTo(currentBase);
            if (baseComparison > 0)
                return true;
            if (baseComparison < 0)
                return false;

            if (manifest.Version.Equals(BuildInfo.Version, StringComparison.OrdinalIgnoreCase))
                return false;

            if (manifest.PublishedAt.ToUniversalTime() > BuildInfo.BuildDateUtc)
                return true;

            return !BuildInfo.Channel.Equals(manifest.Channel, StringComparison.OrdinalIgnoreCase);
        }

        private static Version ParseVersion(string value)
        {
            return Version.TryParse(value, out var version) ? version : new Version(0, 0);
        }

        private static string GetWindowsRuntime()
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        }
    }
}
```

- [ ] **Step 3: Create `src/Models/UpdateInstaller.cs`**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public static class UpdateInstaller
    {
        public static async Task DownloadAndInstallAsync(UpdateAvailable update)
        {
            var asset = update.Asset ?? throw new InvalidOperationException("No Windows self-update package is available.");
            if (string.IsNullOrWhiteSpace(asset.Sha256))
                throw new InvalidOperationException("Update package checksum is missing.");

            var versionDir = Path.Combine(Native.OS.DataDir, "updates", Sanitize(update.Manifest.Version));
            Directory.CreateDirectory(versionDir);

            var zipPath = Path.Combine(versionDir, asset.FileName);
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var input = await client.GetStreamAsync(asset.Url).ConfigureAwait(false))
            await using (var output = File.Create(zipPath))
            {
                await input.CopyToAsync(output).ConfigureAwait(false);
            }

            VerifySha256(zipPath, asset.Sha256);

            var updaterPath = Path.Combine(versionDir, "sourcegit-updater.exe");
            ExtractUpdater(zipPath, updaterPath);
            LaunchUpdater(updaterPath, zipPath);
        }

        private static void VerifySha256(string file, string expected)
        {
            using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!actual.Equals(expected.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                File.Delete(file);
                throw new InvalidOperationException("Update package checksum mismatch.");
            }
        }

        private static void ExtractUpdater(string zipPath, string updaterPath)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("sourcegit-updater.exe") ?? throw new InvalidOperationException("Update package does not contain sourcegit-updater.exe.");
            entry.ExtractToFile(updaterPath, true);
        }

        private static void LaunchUpdater(string updaterPath, string zipPath)
        {
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var start = new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"--package {Quote(zipPath)} --target {Quote(appDir)} --exe SourceGit.exe",
                WorkingDirectory = Path.GetDirectoryName(updaterPath)!,
                UseShellExecute = true,
            };
            Process.Start(start);
            App.Quit(0);
        }

        private static string Sanitize(string version)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                version = version.Replace(c, '_');
            return version;
        }

        private static string Quote(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }
    }
}
```

- [ ] **Step 4: Remove legacy JSON registration**

When `src/Models/SelfUpdate.cs` no longer contains `Models.Version`, remove this line from `src/App.JsonCodeGen.cs`:

```csharp
    [JsonSerializable(typeof(Models.Version))]
```

- [ ] **Step 5: Modify `App.Check4Update` in `src/App.axaml.cs`**

Replace the old GitHub Pages logic inside `Check4Update` with:

```csharp
var pref = ViewModels.Preferences.Instance;
var channel = Models.UpdateChannels.Normalize(pref.UpdateChannel);
var result = await Models.UpdateChecker.CheckAsync(channel);
if (result is Models.AlreadyUpToDate)
{
    if (manually)
        ShowSelfUpdateResult(result);
    return;
}

if (!manually && result is Models.UpdateAvailable available && pref.IsUpdateIgnored(available.Manifest.Channel, available.Manifest.Version))
    return;

ShowSelfUpdateResult(result);
```

Keep the existing try/catch behavior: manual errors show `SelfUpdateFailed`; startup errors remain silent.

- [ ] **Step 6: Build-check Task 2**

Run:

```powershell
dotnet build SourceGit.slnx --no-restore
```

Expected: compile succeeds or failures are unrelated to this task. Fix missing using/import or DTO source-gen errors before moving on.

- [ ] **Step 6: Commit Task 2**

```powershell
git add src/Models/SelfUpdate.cs src/Models/UpdateChecker.cs src/Models/UpdateInstaller.cs src/App.axaml.cs
git commit -m "feat: Check GitLab update manifests(检查 GitLab 更新清单)"
```

---

### Task 3: Preferences And Update Dialog UI

**Files:**
- Modify: `src/ViewModels/Preferences.cs`
- Modify: `src/Views/Preferences.axaml`
- Modify: `src/Views/SelfUpdate.axaml`
- Modify: `src/Views/SelfUpdate.axaml.cs`
- Modify: `src/Resources/Locales/en_US.axaml`
- Modify: `src/Resources/Locales/zh_CN.axaml`

- [ ] **Step 1: Add update channel preferences in `src/ViewModels/Preferences.cs`**

Add properties near `Check4UpdatesOnStartup`:

```csharp
public int UpdateChannel
{
    get => _updateChannel;
    set => SetProperty(ref _updateChannel, value);
}

public string IgnoreUpdateKey
{
    get => _ignoreUpdateKey;
    set => SetProperty(ref _ignoreUpdateKey, value);
}

public bool IsUpdateIgnored(string channel, string version)
{
    return _ignoreUpdateKey.Equals($"{channel}:{version}", StringComparison.Ordinal);
}

public void IgnoreUpdate(string channel, string version)
{
    IgnoreUpdateKey = $"{channel}:{version}";
}
```

Keep the old `IgnoreUpdateTag` property for backward compatibility, but make it redirect to `_ignoreUpdateKey`:

```csharp
public string IgnoreUpdateTag
{
    get => _ignoreUpdateKey;
    set => SetProperty(ref _ignoreUpdateKey, value);
}
```

Add fields near `_check4UpdatesOnStartup`:

```csharp
private int _updateChannel = 0;
private string _ignoreUpdateKey = string.Empty;
```

Remove the old `_ignoreUpdateTag` field after the redirect property compiles.

- [ ] **Step 2: Add channel selector to `src/Views/Preferences.axaml`**

Increase the General tab grid row count from:

```xml
RowDefinitions="32,32,32,32,32,32,32,32,32,32,32,32,32,32,Auto"
```

to:

```xml
RowDefinitions="32,32,32,32,32,32,32,32,32,32,32,32,32,32,32,Auto"
```

Insert a row before `ExportPerfettoTraceOnExit`:

```xml
<TextBlock Grid.Row="14" Grid.Column="0"
           Text="{DynamicResource Text.Preferences.General.UpdateChannel}"
           IsVisible="{x:Static s:App.IsCheckForUpdateCommandVisible}"
           HorizontalAlignment="Right"
           Margin="0,0,16,0"/>
<ComboBox Grid.Row="14" Grid.Column="1"
          MinHeight="28"
          Padding="8,0"
          IsVisible="{x:Static s:App.IsCheckForUpdateCommandVisible}"
          SelectedIndex="{Binding UpdateChannel, Mode=TwoWay}">
  <ComboBoxItem Content="{DynamicResource Text.UpdateChannel.Stable}"/>
  <ComboBoxItem Content="{DynamicResource Text.UpdateChannel.Nightly}"/>
</ComboBox>
```

Move the existing `ExportPerfettoTraceOnExit` checkbox from row 14 to row 15.

- [ ] **Step 3: Update `src/Views/SelfUpdate.axaml` data template**

Change `DataTemplate DataType="m:Version"` to:

```xml
<DataTemplate DataType="m:UpdateAvailable">
```

Add channel text under current version:

```xml
<TextBlock Margin="0,2,0,0">
  <Run Text="{DynamicResource Text.SelfUpdate.Channel}" FontWeight="Bold" Foreground="{DynamicResource Brush.FG2}"/>
  <Run Text="{Binding ChannelName, Mode=OneWay}"/>
</TextBlock>
```

Change the primary button text binding to use an install string on Windows by adding a second primary button:

```xml
<Button Classes="flat primary"
        Height="30"
        Click="InstallUpdate"
        IsVisible="{Binding CanInstall}"
        HorizontalContentAlignment="Center"
        VerticalContentAlignment="Center">
  <StackPanel Orientation="Horizontal">
    <Path Width="12" Height="12" Data="{StaticResource Icons.SoftwareUpdate}" Fill="{DynamicResource AccentButtonForeground}"/>
    <TextBlock Text="{DynamicResource Text.SelfUpdate.Install}" Margin="8,0,0,0" Foreground="{DynamicResource AccentButtonForeground}"/>
  </StackPanel>
</Button>
```

Keep the existing download button but set `IsVisible="{Binding !CanInstall}"`.

- [ ] **Step 4: Update `src/Views/SelfUpdate.axaml.cs`**

Change `UpdateInfoView.OnDataContextChanged` to:

```csharp
if (DataContext is Models.UpdateAvailable update)
    Text = update.Body;
```

Change `GotoDownload` to:

```csharp
private void GotoDownload(object sender, RoutedEventArgs e)
{
    if (sender is Button { DataContext: Models.UpdateAvailable update } && !string.IsNullOrWhiteSpace(update.ReleasePageUrl))
        Native.OS.OpenBrowser(update.ReleasePageUrl);
    else
        Native.OS.OpenBrowser("http://gitlab.zjhuayu.top/all/sourcegit/-/releases");

    e.Handled = true;
}
```

Add:

```csharp
private async void InstallUpdate(object sender, RoutedEventArgs e)
{
    if (sender is Button { DataContext: Models.UpdateAvailable update })
    {
        try
        {
            await Models.UpdateInstaller.DownloadAndInstallAsync(update);
        }
        catch (Exception ex)
        {
            DataContext = new ViewModels.SelfUpdate { Data = new Models.SelfUpdateFailed(ex) };
        }
    }

    e.Handled = true;
}
```

Change `IgnoreThisVersion` to:

```csharp
if (sender is Button { DataContext: Models.UpdateAvailable update })
    ViewModels.Preferences.Instance.IgnoreUpdate(update.Manifest.Channel, update.Manifest.Version);
```

- [ ] **Step 5: Add localization keys**

In `src/Resources/Locales/en_US.axaml`, add near the preferences/update strings:

```xml
<x:String x:Key="Text.Preferences.General.UpdateChannel" xml:space="preserve">Update Channel</x:String>
<x:String x:Key="Text.UpdateChannel.Stable" xml:space="preserve">Stable</x:String>
<x:String x:Key="Text.UpdateChannel.Nightly" xml:space="preserve">Nightly</x:String>
<x:String x:Key="Text.SelfUpdate.Channel" xml:space="preserve">Channel: </x:String>
<x:String x:Key="Text.SelfUpdate.Install" xml:space="preserve">Install Update</x:String>
```

In `src/Resources/Locales/zh_CN.axaml`, add:

```xml
<x:String x:Key="Text.Preferences.General.UpdateChannel" xml:space="preserve">更新渠道</x:String>
<x:String x:Key="Text.UpdateChannel.Stable" xml:space="preserve">稳定版</x:String>
<x:String x:Key="Text.UpdateChannel.Nightly" xml:space="preserve">每夜版</x:String>
<x:String x:Key="Text.SelfUpdate.Channel" xml:space="preserve">更新渠道 ：</x:String>
<x:String x:Key="Text.SelfUpdate.Install" xml:space="preserve">安    装</x:String>
```

- [ ] **Step 6: Build and localization check Task 3**

Run:

```powershell
dotnet build SourceGit.slnx --no-restore
node build/scripts/localization-check.js
```

Expected: build compiles; localization check accepts fallback/inclusion pattern or reports no missing required base keys. Fix XAML binding or missing key errors before moving on.

- [ ] **Step 7: Commit Task 3**

```powershell
git add src/ViewModels/Preferences.cs src/Views/Preferences.axaml src/Views/SelfUpdate.axaml src/Views/SelfUpdate.axaml.cs src/Resources/Locales/en_US.axaml src/Resources/Locales/zh_CN.axaml
git commit -m "feat: Add update channel UI(新增更新渠道界面)"
```

---

### Task 4: SourceGit Updater Project

**Files:**
- Create: `src/SourceGit.Updater/SourceGit.Updater.csproj`
- Create: `src/SourceGit.Updater/Program.cs`
- Create: `src/SourceGit.Updater/SourceGitUpdater.cs`
- Create: `src/SourceGit.Updater/FileUtils.cs`
- Create: `src/SourceGit.Updater/UpdaterUI.cs`
- Create: `src/SourceGit.Updater/Win32UI.cs`
- Create: `src/SourceGit.Updater/app.manifest`
- Modify: `SourceGit.slnx`

- [ ] **Step 1: Copy updater source files from GameUpdater**

Copy these source files as a starting point:

```powershell
Copy-Item 'F:\work\hygames\Tools\GameUpdater\FileUtils.cs' 'src\SourceGit.Updater\FileUtils.cs'
Copy-Item 'F:\work\hygames\Tools\GameUpdater\UpdaterUI.cs' 'src\SourceGit.Updater\UpdaterUI.cs'
Copy-Item 'F:\work\hygames\Tools\GameUpdater\Win32UI.cs' 'src\SourceGit.Updater\Win32UI.cs'
Copy-Item 'F:\work\hygames\Tools\GameUpdater\GameUpdater.cs' 'src\SourceGit.Updater\SourceGitUpdater.cs'
Copy-Item 'F:\work\hygames\Tools\GameUpdater\app.manifest' 'src\SourceGit.Updater\app.manifest'
```

Then replace namespace `GameUpdater` with `SourceGit.Updater` in copied files.

- [ ] **Step 2: Create `src/SourceGit.Updater/SourceGit.Updater.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AssemblyName>sourcegit-updater</AssemblyName>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>

  <ItemGroup>
    <EmbeddedResource Include="..\App.ico" Link="icon.ico" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `src/SourceGit.Updater/Program.cs`**

```csharp
namespace SourceGit.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var updater = new SourceGitUpdater(args);
        updater.Run();
    }
}
```

- [ ] **Step 4: Adapt `SourceGitUpdater.cs` constants and arguments**

Make these targeted changes:

```csharp
private string ZipPath;
private string ExeName;
private string TargetPath;
private string TempPath;
private string BackupPath;
private string NewVersionPath;
private const string KeepSubDir = "data";
private const string UpdaterFileName = "sourcegit-updater.exe";
```

Constructor:

```csharp
public SourceGitUpdater(string[] args)
{
    ZipPath = ExeName = TargetPath = TempPath = BackupPath = NewVersionPath = string.Empty;
    ParseArguments(args);
    m_ui = new UpdaterUI();
}
```

Add argument parsing:

```csharp
private void ParseArguments(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var key = args[i];
        var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
        if (key.Equals("--package", StringComparison.OrdinalIgnoreCase))
            ZipPath = value;
        else if (key.Equals("--target", StringComparison.OrdinalIgnoreCase))
            TargetPath = value;
        else if (key.Equals("--exe", StringComparison.OrdinalIgnoreCase))
            ExeName = value;

        if (key.StartsWith("--", StringComparison.Ordinal))
            i++;
    }
}
```

- [ ] **Step 5: Replace game directory detection**

Replace `UpdateProjectPathes` with SourceGit target initialization:

```csharp
private void InitializePaths()
{
    if (string.IsNullOrWhiteSpace(TargetPath))
        throw new InvalidOperationException("未指定 SourceGit 安装目录。");

    TargetPath = Path.GetFullPath(TargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    if (string.IsNullOrWhiteSpace(ExeName))
        ExeName = "SourceGit.exe";

    if (!File.Exists(Path.Combine(TargetPath, ExeName)))
        throw new InvalidOperationException($"未找到 SourceGit 主程序：{Path.Combine(TargetPath, ExeName)}");

    var parent = Path.GetDirectoryName(TargetPath) ?? throw new InvalidOperationException("安装目录无效。");
    var name = Path.GetFileName(TargetPath);
    TempPath = Path.Combine(parent, $"{name}_Temp");
    BackupPath = Path.Combine(parent, $"{name}_Backup");
    NewVersionPath = Path.Combine(parent, $"{name}_New");

    if (string.IsNullOrWhiteSpace(ZipPath))
    {
        var zipFiles = Directory.GetFiles(parent, "sourcegit_*.zip", SearchOption.TopDirectoryOnly).OrderBy(x => x).ToList();
        if (zipFiles.Count > 0)
            ZipPath = zipFiles[^1];
    }

    if (string.IsNullOrWhiteSpace(ZipPath) || !File.Exists(ZipPath))
        throw new FileNotFoundException($"未找到更新包：{ZipPath}");
}
```

Call `InitializePaths()` from `OnFormLoaded` before `RunUpdateProcess`.

- [ ] **Step 6: Adapt zip extraction**

In `ExtractZipFiles`, remove Unity `_Data` exe detection. Instead:

```csharp
var baseDir = "SourceGit/";
if (!allFiles.Contains("SourceGit/SourceGit.exe"))
    throw new Exception("更新包内容错误，缺少 SourceGit/SourceGit.exe");
```

Keep the existing path traversal guard before `ExtractToFile`.

Change updater self-update lookup from `GameUpdater.exe` to:

```csharp
var updaterEntry = archive.Entries.FirstOrDefault(e =>
    e.FullName.Equals(UpdaterFileName, StringComparison.OrdinalIgnoreCase));
```

Generate versioned updater names as:

```csharp
var newUpdaterName = $"sourcegit-updater_{version}.exe";
```

Clean old updaters with:

```csharp
var updaterFiles = Directory.GetFiles(rootPath, "sourcegit-updater*.exe", SearchOption.TopDirectoryOnly);
```

- [ ] **Step 7: Change updater UI text**

In `UpdaterUI.cs`, change:

```csharp
lpszClassName = "SourceGitUpdaterClass"
```

and window title:

```csharp
"SourceGit 自动更新器"
```

Change the embedded icon resource name to:

```csharp
var resourceName = "SourceGit.Updater.icon.ico";
```

In `SourceGitUpdater.cs`, replace user-facing strings:

- `游戏` -> `SourceGit`
- `游戏主程序` -> `SourceGit 主程序`
- `正在启动游戏...` -> `正在启动 SourceGit...`

- [ ] **Step 8: Add updater project to `SourceGit.slnx`**

Add under the `/src/` folder:

```xml
        <Project Path="src/SourceGit.Updater/SourceGit.Updater.csproj" />
```

- [ ] **Step 9: Build-check Task 4**

Run:

```powershell
dotnet publish src/SourceGit.Updater/SourceGit.Updater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o build/updater-test
```

Expected: `build/updater-test/sourcegit-updater.exe` exists.

- [ ] **Step 10: Commit Task 4**

```powershell
git add src/SourceGit.Updater SourceGit.slnx
git commit -m "feat: Add SourceGit Windows updater(新增 SourceGit Windows 更新器)"
```

---

### Task 5: GitLab Packaging Scripts

**Files:**
- Create: `build/scripts/package.gitlab.win.ps1`
- Create: `build/scripts/gitlab/package-manifest.ps1`
- Create: `build/scripts/gitlab/release-assets.sh`
- Create: `build/scripts/gitlab/cleanup-nightly.sh`
- Modify: `SourceGit.slnx`

- [ ] **Step 1: Create `build/scripts/package.gitlab.win.ps1`**

```powershell
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:VERSION)) { throw "VERSION is required" }
if ([string]::IsNullOrWhiteSpace($env:RUNTIME)) { throw "RUNTIME is required" }
if ([string]::IsNullOrWhiteSpace($env:SOURCEGIT_BUILD)) { $env:SOURCEGIT_BUILD = "build/SourceGit" }
if ([string]::IsNullOrWhiteSpace($env:UPDATER_BUILD)) { $env:UPDATER_BUILD = "build/Updater" }

$packageRoot = "build/package-$($env:RUNTIME)"
$sourceGitDir = Join-Path $packageRoot "SourceGit"
Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $sourceGitDir | Out-Null

Copy-Item -Path (Join-Path $env:SOURCEGIT_BUILD "*") -Destination $sourceGitDir -Recurse -Force
Remove-Item -Path (Join-Path $sourceGitDir "*.pdb") -Force -ErrorAction SilentlyContinue

$updater = Join-Path $env:UPDATER_BUILD "sourcegit-updater.exe"
if (!(Test-Path -LiteralPath $updater)) { throw "Missing updater: $updater" }
Copy-Item -LiteralPath $updater -Destination (Join-Path $packageRoot "sourcegit-updater.exe") -Force

$zip = "build/sourcegit_$($env:VERSION).$($env:RUNTIME).zip"
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zip -Force
Write-Host "Created $zip"
```

- [ ] **Step 2: Create `build/scripts/gitlab/package-manifest.ps1`**

```powershell
$ErrorActionPreference = "Stop"

$version = $env:VERSION
$baseVersion = $env:BASE_VERSION
$channel = $env:UPDATE_CHANNEL
$commit = $env:CI_COMMIT_SHA
$pipelineId = $env:CI_PIPELINE_ID
$pipelineIid = $env:CI_PIPELINE_IID
$publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$packageBaseUrl = "$($env:CI_API_V4_URL)/projects/$($env:CI_PROJECT_ID)/packages/generic/sourcegit/$version"

$assets = @()
Get-ChildItem -LiteralPath "build" -File | Where-Object {
    $_.Name -like "sourcegit_$version.*" -or $_.Name -like "sourcegit-$version*"
} | ForEach-Object {
    $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
    $runtime = if ($_.Name -match "\.(win-x64|win-arm64|osx-x64|osx-arm64)\.zip$") { $Matches[1] } elseif ($_.Name -match "linux\.x64|linux-x64") { "linux-x64" } elseif ($_.Name -match "linux\.arm64|linux-arm64") { "linux-arm64" } else { "unknown" }
    $kind = if ($runtime.StartsWith("win-") -and $_.Extension -eq ".zip") { "self-update-zip" } elseif ($_.Extension -eq ".zip") { "zip" } elseif ($_.Extension -eq ".deb") { "deb" } elseif ($_.Extension -eq ".rpm") { "rpm" } elseif ($_.Name.EndsWith(".AppImage")) { "appimage" } else { "file" }
    $assets += [ordered]@{
        runtime = $runtime
        kind = $kind
        fileName = $_.Name
        url = "$packageBaseUrl/$($_.Name)"
        sha256 = $sha
    }
}

$manifest = [ordered]@{
    version = $version
    baseVersion = $baseVersion
    channel = $channel
    commit = $commit
    pipelineId = $pipelineId
    pipelineIid = $pipelineIid
    publishedAt = $publishedAt
    releaseNotes = "SourceGit $version`n`nCommit: $commit`nPipeline: $pipelineIid"
    assets = $assets
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "build/sourcegit-update.json" -Encoding UTF8
```

- [ ] **Step 3: Create `build/scripts/gitlab/release-assets.sh`**

```bash
#!/usr/bin/env bash
set -euo pipefail

: "${VERSION:?VERSION is required}"
: "${UPDATE_CHANNEL:?UPDATE_CHANNEL is required}"
: "${CI_API_V4_URL:?CI_API_V4_URL is required}"
: "${CI_PROJECT_ID:?CI_PROJECT_ID is required}"
: "${CI_JOB_TOKEN:?CI_JOB_TOKEN is required}"

PACKAGE_NAME="sourcegit"
PACKAGE_URL="${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/packages/generic/${PACKAGE_NAME}/${VERSION}"
CHANNEL_TAG="${UPDATE_CHANNEL}-release"
CHANNEL_NAME="SourceGit ${UPDATE_CHANNEL}"

upload_file() {
  local file="$1"
  local name
  name="$(basename "$file")"
  curl --fail --location --header "JOB-TOKEN: ${CI_JOB_TOKEN}" --upload-file "$file" "${PACKAGE_URL}/${name}"
}

for file in build/sourcegit* build/*.AppImage build/*.deb build/*.rpm; do
  [ -f "$file" ] || continue
  upload_file "$file"
done

release_payload() {
  local tag="$1"
  local name="$2"
  python3 - "$tag" "$name" <<'PY'
import json, os, sys
tag, name = sys.argv[1], sys.argv[2]
version = os.environ["VERSION"]
package_url = f'{os.environ["CI_API_V4_URL"]}/projects/{os.environ["CI_PROJECT_ID"]}/packages/generic/sourcegit/{version}'
links = [{
    "name": "sourcegit-update.json",
    "url": f"{package_url}/sourcegit-update.json",
    "direct_asset_path": "/sourcegit-update.json",
    "link_type": "package",
}]
for fn in os.listdir("build"):
    if fn.startswith("sourcegit_") or fn.endswith((".AppImage", ".deb", ".rpm")):
        links.append({
            "name": fn,
            "url": f"{package_url}/{fn}",
            "direct_asset_path": f"/{fn}",
            "link_type": "package",
        })
print(json.dumps({
    "name": name,
    "tag_name": tag,
    "ref": os.environ["CI_COMMIT_SHA"],
    "description": f"SourceGit {version}",
    "assets": {"links": links},
}))
PY
}

create_release() {
  local tag="$1"
  local name="$2"
  local payload
  payload="$(release_payload "$tag" "$name")"
  curl --fail --request POST --header "JOB-TOKEN: ${CI_JOB_TOKEN}" --header "Content-Type: application/json" --data "$payload" "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/releases"
}

replace_channel_release() {
  local tag="$1"
  local name="$2"
  if curl --silent --fail --header "JOB-TOKEN: ${CI_JOB_TOKEN}" "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/releases/${tag}" >/dev/null; then
    curl --fail --request DELETE --header "JOB-TOKEN: ${CI_JOB_TOKEN}" "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/releases/${tag}"
  fi
  create_release "$tag" "$name"
}

create_release_if_missing() {
  local tag="$1"
  local name="$2"
  if curl --silent --fail --header "JOB-TOKEN: ${CI_JOB_TOKEN}" "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/releases/${tag}" >/dev/null; then
    echo "Release ${tag} already exists; keeping immutable history."
    return 0
  fi
  create_release "$tag" "$name"
}

replace_channel_release "$CHANNEL_TAG" "$CHANNEL_NAME"
if [ "$UPDATE_CHANNEL" = "stable" ]; then
  create_release_if_missing "stable-v${VERSION}" "SourceGit stable ${VERSION}"
fi
```

- [ ] **Step 4: Create `build/scripts/gitlab/cleanup-nightly.sh`**

```bash
#!/usr/bin/env bash
set -euo pipefail

[ "${UPDATE_CHANNEL:-}" = "nightly" ] || exit 0
TOKEN="${GITLAB_RELEASE_TOKEN:-${CI_JOB_TOKEN:?CI_JOB_TOKEN is required}}"
HEADER="JOB-TOKEN: ${TOKEN}"
[ -n "${GITLAB_RELEASE_TOKEN:-}" ] && HEADER="PRIVATE-TOKEN: ${TOKEN}"

mkdir -p build
curl --fail --header "$HEADER" --output build/packages.json "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/packages?package_name=sourcegit&per_page=100&order_by=created_at&sort=desc"
python3 - build/packages.json <<'PY' > build/nightly-packages-to-delete.txt
import json, re, sys
with open(sys.argv[1], encoding="utf-8") as fh:
    packages = json.load(fh)
nightly = [p for p in packages if re.search(r"-nightly\.", p.get("version", ""))]
for pkg in nightly[30:]:
    print(pkg["id"])
PY

while read -r id; do
  [ -n "$id" ] || continue
  curl --fail --request DELETE --header "$HEADER" "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/packages/${id}"
done < build/nightly-packages-to-delete.txt
```

- [ ] **Step 5: Add scripts to `SourceGit.slnx`**

Add file entries under `/build/scripts/` or a new `/build/scripts/gitlab/` folder for all new scripts.

- [ ] **Step 6: Script syntax checks**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/scripts/package.gitlab.win.ps1
```

Expected: it fails fast with `VERSION is required`, not a parser error.

Run:

```powershell
bash -n build/scripts/gitlab/release-assets.sh
bash -n build/scripts/gitlab/cleanup-nightly.sh
```

Expected: no syntax errors.

- [ ] **Step 7: Commit Task 5**

```powershell
git add build/scripts/package.gitlab.win.ps1 build/scripts/gitlab SourceGit.slnx
git commit -m "ci: Add GitLab packaging scripts(新增 GitLab 打包脚本)"
```

---

### Task 6: GitLab Pipeline

**Files:**
- Create: `.gitlab-ci.yml`
- Modify: `SourceGit.slnx`

- [ ] **Step 1: Create `.gitlab-ci.yml`**

Use this initial pipeline:

```yaml
stages:
  - build
  - package
  - release

variables:
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1"
  RELEASE_CHANNEL:
    value: "nightly"
    options:
      - "nightly"
      - "stable"
    description: "Release channel to publish"

workflow:
  rules:
    - if: '$CI_PIPELINE_SOURCE == "schedule" && $CI_COMMIT_BRANCH == "huayu"'
      variables:
        RELEASE_CHANNEL: "nightly"
    - if: '$CI_PIPELINE_SOURCE == "web"'
    - when: never

build_packages:
  stage: build
  image: mcr.microsoft.com/dotnet/sdk:10.0
  before_script:
    - apt-get update
    - apt-get install -y git zip unzip curl wget bash nodejs npm dpkg-dev fakeroot desktop-file-utils rpm libfuse2 file build-essential binutils
    - git submodule update --init --recursive
    - export BASE_VERSION="$(tr -d '\r\n' < VERSION)"
    - export SHORT_SHA="${CI_COMMIT_SHORT_SHA}"
    - |
      if [ "$RELEASE_CHANNEL" = "nightly" ]; then
        export VERSION="${BASE_VERSION}-nightly.$(date -u +%Y%m%d).${CI_PIPELINE_IID}.${SHORT_SHA}"
      else
        export VERSION="${BASE_VERSION}"
      fi
    - echo "VERSION=$VERSION" | tee build.env
    - echo "BASE_VERSION=$BASE_VERSION" | tee -a build.env
  script:
    - dotnet restore SourceGit.slnx
    - dotnet build SourceGit.slnx -c Release --no-restore -p:DisableAOT=true -p:SourceGitUpdateChannel="$RELEASE_CHANNEL" -p:SourceGitUpdateVersion="$VERSION" -p:SourceGitBaseVersion="$BASE_VERSION" -p:SourceGitPipelineIid="$CI_PIPELINE_IID" -p:SourceGitCommit="$CI_COMMIT_SHA" -p:SourceGitBuildDate="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    - mkdir -p build/out
    - for runtime in win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64; do dotnet publish src/SourceGit.csproj -c Release -o "build/publish/$runtime" -r "$runtime" --self-contained true -p:DisableAOT=true -p:SourceGitUpdateChannel="$RELEASE_CHANNEL" -p:SourceGitUpdateVersion="$VERSION" -p:SourceGitBaseVersion="$BASE_VERSION" -p:SourceGitPipelineIid="$CI_PIPELINE_IID" -p:SourceGitCommit="$CI_COMMIT_SHA" -p:SourceGitBuildDate="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; done
    - for runtime in win-x64 win-arm64; do dotnet publish src/SourceGit.Updater/SourceGit.Updater.csproj -c Release -o "build/updater/$runtime" -r "$runtime" --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true; done
    - for runtime in win-x64 win-arm64; do SOURCEGIT_BUILD="build/publish/$runtime" UPDATER_BUILD="build/updater/$runtime" VERSION="$VERSION" RUNTIME="$runtime" pwsh build/scripts/package.gitlab.win.ps1; done
    - for runtime in osx-x64 osx-arm64; do mkdir -p build/SourceGit && cp -R "build/publish/$runtime/." build/SourceGit/ && VERSION="$VERSION" RUNTIME="$runtime" ./build/scripts/package.osx-app.sh && rm -rf build/SourceGit; done
    - for runtime in linux-x64 linux-arm64; do mkdir -p build/SourceGit && cp -R "build/publish/$runtime/." build/SourceGit/ && VERSION="$VERSION" RUNTIME="$runtime" APPIMAGE_EXTRACT_AND_RUN=1 ./build/scripts/package.linux.sh && rm -rf build/SourceGit; done
    - VERSION="$VERSION" BASE_VERSION="$BASE_VERSION" UPDATE_CHANNEL="$RELEASE_CHANNEL" pwsh build/scripts/gitlab/package-manifest.ps1
  artifacts:
    reports:
      dotenv: build.env
    paths:
      - build/sourcegit*
      - build/*.AppImage
      - build/*.deb
      - build/*.rpm
      - build/sourcegit-update.json
    expire_in: 7 days

release_packages:
  stage: release
  image: alpine:3.20
  needs:
    - job: build_packages
      artifacts: true
  before_script:
    - apk add --no-cache bash curl python3
  script:
    - VERSION="$VERSION" UPDATE_CHANNEL="$RELEASE_CHANNEL" bash build/scripts/gitlab/release-assets.sh
    - VERSION="$VERSION" UPDATE_CHANNEL="$RELEASE_CHANNEL" bash build/scripts/gitlab/cleanup-nightly.sh
```

- [ ] **Step 2: Review pipeline risks**

Check these explicitly:

- `pwsh` availability in `mcr.microsoft.com/dotnet/sdk:10.0`; if missing, install PowerShell or rewrite package scripts to Bash.
- `package.linux.sh` support for running inside this image and for arm64 packaging; if it requires Ubuntu 20.04-specific packages, switch the job image to `ubuntu:20.04` and install .NET SDK manually.
- AppImage tooling execution on the GitLab Linux host; keep `APPIMAGE_EXTRACT_AND_RUN=1`.

- [ ] **Step 3: Add `.gitlab-ci.yml` to `SourceGit.slnx`**

Add:

```xml
        <File Path=".gitlab-ci.yml"/>
```

under the workflow folder or `/files/`.

- [ ] **Step 4: CI syntax checks**

Run local checks:

```powershell
rg -n "TODO|TBD|https://gitlab\\.zjhuayu\\.top" .gitlab-ci.yml build/scripts/gitlab build/scripts/package.gitlab.win.ps1
bash -n build/scripts/gitlab/release-assets.sh
bash -n build/scripts/gitlab/cleanup-nightly.sh
```

Expected: no TODO/TBD or stale HTTPS host references; no shell syntax errors.

- [ ] **Step 5: Commit Task 6**

```powershell
git add .gitlab-ci.yml SourceGit.slnx
git commit -m "ci: Add GitLab release pipeline(新增 GitLab 发布流水线)"
```

---

### Task 7: End-To-End Packaging Verification

**Files:**
- No production edits expected unless verification finds issues.

- [ ] **Step 1: Build SourceGit and updater**

Run:

```powershell
dotnet build SourceGit.slnx
dotnet publish src/SourceGit.csproj -c Release -r win-x64 -o build/e2e/SourceGit -p:DisableAOT=true -p:SourceGitUpdateChannel=stable -p:SourceGitUpdateVersion=2026.13-test -p:SourceGitBaseVersion=2026.13
dotnet publish src/SourceGit.Updater/SourceGit.Updater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o build/e2e/Updater
```

Expected: both publish commands exit 0 and produce `build/e2e/SourceGit/SourceGit.exe` and `build/e2e/Updater/sourcegit-updater.exe`.

- [ ] **Step 2: Build a Windows update zip**

Run:

```powershell
$env:VERSION = "2026.13-test"
$env:RUNTIME = "win-x64"
$env:SOURCEGIT_BUILD = "build/e2e/SourceGit"
$env:UPDATER_BUILD = "build/e2e/Updater"
powershell -NoProfile -ExecutionPolicy Bypass -File build/scripts/package.gitlab.win.ps1
```

Inspect:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::OpenRead("build/sourcegit_2026.13-test.win-x64.zip").Entries.FullName | Sort-Object
```

Expected: includes `sourcegit-updater.exe` and `SourceGit/SourceGit.exe`.

- [ ] **Step 3: Create updater smoke-test directories**

Run:

```powershell
$root = Resolve-Path "build/e2e"
$old = Join-Path $root "SmokeRoot\SourceGit"
$new = Join-Path $root "SmokeNew\SourceGit"
Remove-Item -LiteralPath (Join-Path $root "SmokeRoot") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $root "SmokeNew") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $old "data") | Out-Null
"old-data" | Set-Content -LiteralPath (Join-Path $old "data\keep.txt")
"old-version" | Set-Content -LiteralPath (Join-Path $old "old.txt")
Copy-Item -LiteralPath "build/e2e/SourceGit/SourceGit.exe" -Destination (Join-Path $old "SourceGit.exe") -Force
New-Item -ItemType Directory -Force -Path $new | Out-Null
Copy-Item -LiteralPath "build/e2e/SourceGit/SourceGit.exe" -Destination (Join-Path $new "SourceGit.exe") -Force
"new-version" | Set-Content -LiteralPath (Join-Path $new "new.txt")
Copy-Item -LiteralPath "build/e2e/Updater/sourcegit-updater.exe" -Destination (Join-Path $root "SmokeNew\sourcegit-updater.exe") -Force
Compress-Archive -Path (Join-Path $root "SmokeNew\*") -DestinationPath (Join-Path $root "smoke-update.zip") -Force
```

- [ ] **Step 4: Run updater smoke test**

Run:

```powershell
& "build/e2e/Updater/sourcegit-updater.exe" --package "build/e2e/smoke-update.zip" --target "build/e2e/SmokeRoot/SourceGit" --exe SourceGit.exe
```

After updater exits or after closing relaunched SourceGit, verify:

```powershell
Test-Path "build/e2e/SmokeRoot/SourceGit/new.txt"
Get-Content "build/e2e/SmokeRoot/SourceGit/data/keep.txt"
Test-Path "build/e2e/SmokeRoot/SourceGit/old.txt"
```

Expected:

- `new.txt` exists.
- `data/keep.txt` contains `old-data`.
- `old.txt` does not exist.

- [ ] **Step 5: Final checks**

Run:

```powershell
node build/scripts/localization-check.js
git status --short
```

Expected: localization check passes; working tree has only intended implementation files before final commit/PR steps.

---

## Plan Self-Review

- Spec coverage: tasks cover GitLab CI, rolling releases, stable history, nightly retention, channel preferences, Windows updater migration, package layout, HTTP/SHA-256 threat boundary, macOS/Linux browser behavior, and validation.
- Placeholder scan: no `TBD` or open-ended implementation placeholders are intended in this plan.
- Type consistency: `UpdateManifest`, `UpdateAsset`, `GitLabRelease`, `UpdateAvailable`, `UpdateChecker`, and `UpdateInstaller` names are used consistently across tasks.

---

### Follow-Up Task: Skip Scheduled Nightly When Commit Is Already Published

**Files:**
- Modify: `.gitlab-ci.yml`

- [ ] **Step 1: Add a scheduled-nightly preflight job**

Add a `prepare` stage with a `nightly-preflight` job. Compare the current `CI_COMMIT_SHA` with the `commit` value from the existing `nightly-release` manifest, then write `SKIP_NIGHTLY_BUILD=true|false` to `build/release.env`:

```bash
python3 - <<'PY'
import json
import os
import sys
import urllib.request

skip = False
if os.environ.get("UPDATE_CHANNEL") == "nightly" and os.environ.get("CI_PIPELINE_SOURCE") == "schedule":
    api = os.environ.get("CI_API_V4_URL", "").rstrip("/")
    project = os.environ.get("CI_PROJECT_ID", "")
    url = f"{api}/projects/{project}/releases/nightly-release/downloads/sourcegit-update.json"
    request = urllib.request.Request(url)
    token = os.environ.get("GITLAB_RELEASE_TOKEN") or os.environ.get("CI_JOB_TOKEN")
    if os.environ.get("GITLAB_RELEASE_TOKEN"):
        request.add_header("PRIVATE-TOKEN", os.environ["GITLAB_RELEASE_TOKEN"])
    elif os.environ.get("CI_JOB_TOKEN"):
        request.add_header("JOB-TOKEN", os.environ["CI_JOB_TOKEN"])

try:
    with urllib.request.urlopen(request, timeout=10) as response:
        data = json.loads(response.read().decode("utf-8"))
    skip = data.get("commit", "") == os.environ.get("CI_COMMIT_SHA", "")
except Exception:
    skip = False

with open("build/release.env", "w", encoding="utf-8") as fh:
    fh.write(f"SKIP_NIGHTLY_BUILD={'true' if skip else 'false'}\n")
PY
```

- [ ] **Step 2: Make release job honor the skip flag**

Make `build-packages` need `nightly-preflight` artifacts and exit before package installation when `SKIP_NIGHTLY_BUILD=true`. At the top of `release-packages.script`, exit successfully for the same flag:

```bash
if [ "${SKIP_NIGHTLY_BUILD:-false}" = "true" ]; then
  echo "Skipping release because scheduled nightly commit was already published."
  exit 0
fi
```

- [ ] **Step 3: Validate CI syntax**

Run:

```powershell
python -c "import yaml; yaml.safe_load(open('.gitlab-ci.yml', encoding='utf-8')); print('OK .gitlab-ci.yml')"
git diff --check
```

Expected: YAML parses and diff check reports no whitespace errors.
