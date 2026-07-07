using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class MergeConflictBlob
    {
        public class StageEntry
        {
            public string Mode { get; init; } = string.Empty;
            public string ObjectId { get; init; } = string.Empty;
            public int Stage { get; init; }
            public string Path { get; init; } = string.Empty;
        }

        public static async Task<List<StageEntry>> QueryUnmergedEntriesAsync(string repo, CancellationToken cancellation)
        {
            return await QueryStageEntriesAsync(repo, "ls-files -u", cancellation).ConfigureAwait(false);
        }

        public static async Task<List<StageEntry>> QueryConflictEntriesAsync(string repo, CancellationToken cancellation)
        {
            return await QueryStageEntriesAsync(repo, "ls-files -u --resolve-undo", cancellation).ConfigureAwait(false);
        }

        private static async Task<List<StageEntry>> QueryStageEntriesAsync(string repo, string args, CancellationToken cancellation)
        {
            var start = CreateGitStartInfo(repo, args);
            var entries = new List<StageEntry>();

            using var proc = new Process() { StartInfo = start };
            proc.Start();

            while (await proc.StandardOutput.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                var tabIdx = line.IndexOf('\t');
                if (tabIdx <= 0)
                    continue;

                var header = line.Substring(0, tabIdx);
                var path = line.Substring(tabIdx + 1);
                var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !int.TryParse(parts[2], out var stage))
                    continue;

                entries.Add(new StageEntry
                {
                    Mode = parts[0],
                    ObjectId = parts[1],
                    Stage = stage,
                    Path = path,
                });
            }

            await proc.WaitForExitAsync(cancellation).ConfigureAwait(false);
            return proc.ExitCode == 0 ? entries : [];
        }

        public static async Task<MergeTool.MergeFiles> CreateMergeFilesAsync(
            string repo,
            string path,
            string mineName = "",
            string theirName = "",
            bool hasBaseStage = true,
            CancellationToken cancellation = default)
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "SourceGitMerge", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDir);

            try
            {
                var baseFile = hasBaseStage
                    ? await CheckoutStageToTempAsync(repo, path, 1, BuildSideLabel("BASE", string.Empty), outputDir, cancellation).ConfigureAwait(false)
                    : GetEmptyFilePath(outputDir, path, BuildSideLabel("BASE", string.Empty));

                return new MergeTool.MergeFiles
                {
                    Base = baseFile,
                    Local = await CheckoutStageToTempAsync(repo, path, 2, BuildSideLabel("MINE", mineName), outputDir, cancellation).ConfigureAwait(false),
                    Remote = await CheckoutStageToTempAsync(repo, path, 3, BuildSideLabel("THEIR", theirName), outputDir, cancellation).ConfigureAwait(false),
                    Merged = Path.Combine(repo, path),
                    TempDir = outputDir,
                };
            }
            catch
            {
                TryDeleteDirectory(outputDir);
                throw;
            }
        }

        public static void CleanupMergeFiles(MergeTool.MergeFiles files)
        {
            if (!string.IsNullOrEmpty(files?.TempDir))
                TryDeleteDirectory(files.TempDir);
        }

        private static async Task<string> CheckoutStageToTempAsync(
            string repo,
            string path,
            int stage,
            string stageLabel,
            string outputDir,
            CancellationToken cancellation)
        {
            var checkoutRoot = Path.Combine(outputDir, "raw", stage.ToString());
            Directory.CreateDirectory(checkoutRoot);

            var prefix = BuildGitPrefix(checkoutRoot);
            var start = CreateGitStartInfo(repo, $"checkout-index -f --stage={stage} --prefix={prefix.Quoted()} -- {path.Quoted()}");
            using var proc = new Process() { StartInfo = start };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellation);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellation);
            await proc.WaitForExitAsync(cancellation).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                throw new Exception(string.IsNullOrWhiteSpace(stderr) ? $"Unable to checkout stage {stage} for {path}." : stderr.Trim());
            }

            var source = Path.Combine(checkoutRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
                throw new Exception($"Temporary checkout file not found: {source}");

            try
            {
                var target = BuildStageFilePath(outputDir, path, stageLabel);
                File.Move(source, target, true);
                TryDeleteDirectory(checkoutRoot);
                return target;
            }
            catch
            {
                TryDelete(source);
                throw;
            }
        }

        private static ProcessStartInfo CreateGitStartInfo(string repo, string args, bool redirectInput = false)
        {
            var start = new ProcessStartInfo();
            start.WorkingDirectory = repo;
            start.FileName = Native.OS.GitExecutable;
            var builder = new StringBuilder();
            builder.Append("--no-pager -c core.quotepath=off ");
            GitRuntimeConfig.Append(builder, repo);
            builder.Append(args);
            start.Arguments = builder.ToString();
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            start.RedirectStandardInput = redirectInput;
            return start;
        }

        private static string BuildGitPrefix(string dir)
        {
            var full = Path.GetFullPath(dir);
            if (!full.EndsWith(Path.DirectorySeparatorChar))
                full += Path.DirectorySeparatorChar;

            return full.Replace('\\', '/');
        }

        private static string GetEmptyFilePath(string outputDir, string path, string stageLabel)
        {
            var file = BuildStageFilePath(outputDir, path, stageLabel);
            if (!File.Exists(file))
                File.WriteAllBytes(file, []);

            return file;
        }

        private static string BuildStageFilePath(string outputDir, string path, string stageLabel)
        {
            var fileName = GetRealFileName(path);
            var extension = Path.GetExtension(fileName);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            var safeName = SanitizeFileName(nameWithoutExtension, "file");
            var safeExtension = SanitizeExtension(extension);
            var safeStageLabel = SanitizeFileName(stageLabel, "stage");
            return Path.Combine(outputDir, $"{safeName}_{safeStageLabel}{safeExtension}");
        }

        private static string BuildSideLabel(string side, string name)
        {
            var safeSide = SanitizeFileName(side, side);
            var safeName = SanitizeFileName(name, string.Empty);
            return string.IsNullOrEmpty(safeName) ? safeSide : $"{safeSide}_{safeName}";
        }

        private static string GetRealFileName(string path)
        {
            var normalized = path.Replace('\\', '/');
            var idx = normalized.LastIndexOf('/');
            return idx >= 0 ? normalized.Substring(idx + 1) : normalized;
        }

        private static string SanitizeExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return string.Empty;

            var sanitized = SanitizeFileName(extension.TrimStart('.'), string.Empty);
            return string.IsNullOrEmpty(sanitized) ? string.Empty : $".{sanitized}";
        }

        private static string SanitizeFileName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
                return fallback;

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(Math.Min(name.Length, 80));
            foreach (var c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' || c == ':')
                    builder.Append('_');
                else
                    builder.Append(c);

                if (builder.Length >= 80)
                    break;
            }

            return builder.Length == 0 ? fallback : builder.ToString();
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }
    }
}
