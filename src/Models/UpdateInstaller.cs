using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia.Threading;

#nullable enable

namespace SourceGit.Models
{
    public class UpdateInstallProgress
    {
        public string DescriptionKey
        {
            get;
        }

        public double? Percentage
        {
            get;
        }

        public UpdateInstallProgress(string descriptionKey, double? percentage = null)
        {
            DescriptionKey = descriptionKey;
            Percentage = percentage.HasValue ? Math.Clamp(percentage.Value, 0, 100) : null;
        }
    }

    public static class UpdateInstaller
    {
        public static async Task DownloadAndInstallAsync(UpdateAvailable update, IProgress<UpdateInstallProgress>? progress = null)
        {
            if (update == null || update.Asset == null)
                throw new InvalidOperationException("No installable update asset was selected.");

            var asset = update.Asset;
            Report(progress, "SelfUpdate.InstallStage.Preparing");

            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Self update installation is only supported on Windows.");

            if (string.IsNullOrWhiteSpace(asset.Sha256))
                throw new InvalidOperationException("Update asset does not include a SHA256 checksum.");

            var fileName = GetPackageFileName(asset);

            var updateDir = Path.Combine(Native.OS.DataDir, "updates", SanitizePathSegment(update.Manifest.Version));
            Directory.CreateDirectory(updateDir);

            var packagePath = Path.Combine(updateDir, fileName);
            EnsurePathInsideDirectory(updateDir, packagePath);

            Report(progress, "SelfUpdate.InstallStage.Downloading");
            using (var client = new HttpClient())
            using (var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var output = File.Create(packagePath);
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength is > 0)
                    Report(progress, "SelfUpdate.InstallStage.Downloading", 0);

                await CopyToAsync(input, output, contentLength, progress).ConfigureAwait(false);
            }

            Report(progress, "SelfUpdate.InstallStage.Verifying");
            VerifySha256(packagePath, asset.Sha256);

            Report(progress, "SelfUpdate.InstallStage.Extracting");
            var updaterPath = Path.Combine(updateDir, "sourcegit-updater.exe");
            ExtractUpdater(packagePath, updaterPath);

            Report(progress, "SelfUpdate.InstallStage.Launching");
            var targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var start = new ProcessStartInfo(updaterPath)
            {
                UseShellExecute = true,
                Arguments = $"--package {Quote(packagePath)} --target {Quote(targetDir)} --exe SourceGit.exe",
            };

            Process.Start(start);
            await Dispatcher.UIThread.InvokeAsync(() => App.Quit(0));
        }

        private static async Task CopyToAsync(Stream input, Stream output, long? totalBytes, IProgress<UpdateInstallProgress>? progress)
        {
            var buffer = new byte[81920];
            var downloaded = 0L;
            var lastPercentage = -1.0;

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                downloaded += read;

                if (totalBytes is > 0)
                {
                    var percentage = Math.Min(100, downloaded * 100.0 / totalBytes.Value);
                    if (percentage >= 100 || Math.Abs(percentage - lastPercentage) >= 0.1)
                    {
                        lastPercentage = percentage;
                        Report(progress, "SelfUpdate.InstallStage.Downloading", percentage);
                    }
                }
            }

            if (totalBytes is > 0)
                Report(progress, "SelfUpdate.InstallStage.Downloading", 100);
        }

        private static string GetPackageFileName(UpdateAsset asset)
        {
            if (string.IsNullOrWhiteSpace(asset.FileName))
            {
                var fileNameFromUrl = Path.GetFileName(new Uri(asset.Url).LocalPath);
                if (string.IsNullOrWhiteSpace(fileNameFromUrl))
                    throw new InvalidOperationException("Update asset does not include a file name.");

                return fileNameFromUrl;
            }

            if (Path.GetFileName(asset.FileName) != asset.FileName ||
                asset.FileName.Contains(Path.DirectorySeparatorChar) ||
                asset.FileName.Contains(Path.AltDirectorySeparatorChar))
                throw new InvalidOperationException("Update asset file name must not include path separators.");

            return asset.FileName;
        }

        private static void EnsurePathInsideDirectory(string directory, string file)
        {
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullFile = Path.GetFullPath(file);
            if (!fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update package path is outside of the update directory.");
        }

        private static void VerifySha256(string file, string expected)
        {
            using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Downloaded update asset SHA256 checksum mismatch.");
        }

        private static void ExtractUpdater(string packagePath, string updaterPath)
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var entry = zip.GetEntry("sourcegit-updater.exe");
            if (entry == null)
                throw new InvalidOperationException("Update package does not include sourcegit-updater.exe at the archive root.");

            entry.ExtractToFile(updaterPath, true);
        }

        private static string SanitizePathSegment(string value)
        {
            var chars = (string.IsNullOrWhiteSpace(value) ? "update" : value).ToCharArray();
            var invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }

            var segment = new string(chars).Trim();
            if (string.IsNullOrEmpty(segment) ||
                segment.Equals(".", StringComparison.Ordinal) ||
                segment.Equals("..", StringComparison.Ordinal))
                return "update";

            return segment;
        }

        private static string Quote(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static void Report(IProgress<UpdateInstallProgress>? progress, string descriptionKey, double? percentage = null)
        {
            progress?.Report(new UpdateInstallProgress(descriptionKey, percentage));
        }
    }
}
