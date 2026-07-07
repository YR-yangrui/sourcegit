using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class RenderCustomDiff
    {
        private const int RendererTimeoutSeconds = 60;
        private const int RendererKillGraceSeconds = 2;

        public sealed class PreparedInput : IDisposable
        {
            internal PreparedInput(string tempDir, string oldFile, string newFile, string fingerprint)
            {
                TempDir = tempDir;
                OldFile = oldFile;
                NewFile = newFile;
                Fingerprint = fingerprint;
            }

            public string TempDir { get; }
            public string OldFile { get; }
            public string NewFile { get; }
            public string Fingerprint { get; }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                TryDeleteDirectory(TempDir);
            }

            private bool _disposed;
        }

        public RenderCustomDiff(string repo, Models.DiffOption option, Models.CustomDiffRenderer renderer, string title)
        {
            _repo = repo;
            _option = option;
            _renderer = renderer;
            _title = title;
        }

        public async Task<object> RunAsync(CancellationToken cancellationToken = default, PreparedInput input = null)
        {
            var repoPath = SourceGit.Diagnostics.DiagnosticManager.GetRepositoryPath(_repo);
            using var span = SourceGit.Diagnostics.DiagnosticManager.StartSpan(
                "Diff.CustomRenderer",
                "renderer.run",
                SourceGit.Diagnostics.DiagnosticManager.CreateData(
                    ("repo", SourceGit.Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("path", _option.Path),
                    ("title", _title),
                    ("rendererName", _renderer.Name),
                    ("rendererExecutable", _renderer.Executable)));

            var executableExists = File.Exists(_renderer.Executable);
            span.Set("executableExists", executableExists);
            if (!executableExists)
            {
                span.Set("resultType", nameof(Models.CustomDiffError));
                return Error($"Executable does not exist: {_renderer.Executable}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var tempDir = input?.TempDir;
            var oldFile = input?.OldFile;
            var newFile = input?.NewFile;
            if (input == null)
            {
                var tempRoot = Path.Combine(Path.GetTempPath(), "sourcegit-custom-diff");
                CleanupStaleTempDirs(tempRoot);

                tempDir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                oldFile = Path.Combine(tempDir, "old" + GetTempExtension());
                newFile = Path.Combine(tempDir, "new" + GetTempExtension());
            }

            span.Set("tempDir", tempDir);
            span.Set("inputPrepared", input != null);

            var keepTempDir = false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (input == null)
                    await ExportFilesAsync(oldFile, newFile, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                var result = await RunRendererAsync(oldFile, newFile, tempDir, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (!result.IsSuccess)
                {
                    span.Set("resultType", nameof(Models.CustomDiffError));
                    span.Set("stderrLength", result.StdErr?.Length ?? 0);
                    return Error(string.IsNullOrWhiteSpace(result.StdErr) ? "Custom diff renderer failed." : result.StdErr.Trim());
                }

                var parsed = await ParseOutputAsync(result.StdOut, tempDir).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                keepTempDir = ShouldKeepTempDir(parsed, tempDir);
                if (keepTempDir && parsed is Models.HtmlDiff htmlDiff)
                    htmlDiff.TempDirectory = tempDir;

                span.Set("resultType", parsed.GetType().Name);
                span.Set("keepTempDir", keepTempDir);
                if (parsed is Models.HtmlDiff html)
                    span.Set("htmlSource", html.Source?.LocalPath ?? html.Source?.ToString() ?? string.Empty);

                return parsed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                span.MarkCanceled(true);
                throw;
            }
            catch (Exception e)
            {
                span.Set("resultType", nameof(Models.CustomDiffError));
                span.Set("error", e.Message);
                return Error(e.Message);
            }
            finally
            {
                if (!keepTempDir)
                {
                    if (input != null)
                        input.Dispose();
                    else
                        TryDeleteDirectory(tempDir);
                }
            }
        }

        public async Task<PreparedInput> PrepareInputAsync(CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_renderer.Executable))
                return null;

            cancellationToken.ThrowIfCancellationRequested();

            var tempRoot = Path.Combine(Path.GetTempPath(), "sourcegit-custom-diff");
            CleanupStaleTempDirs(tempRoot);

            var tempDir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var oldFile = Path.Combine(tempDir, "old" + GetTempExtension());
            var newFile = Path.Combine(tempDir, "new" + GetTempExtension());

            try
            {
                await ExportFilesAsync(oldFile, newFile, cancellationToken).ConfigureAwait(false);
                var oldHash = await ComputeFileHashAsync(oldFile, cancellationToken).ConfigureAwait(false);
                var newHash = await ComputeFileHashAsync(newFile, cancellationToken).ConfigureAwait(false);
                return new PreparedInput(tempDir, oldFile, newFile, $"{oldHash}:{newHash}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteDirectory(tempDir);
                throw;
            }
            catch
            {
                TryDeleteDirectory(tempDir);
                return null;
            }
        }

        public async Task<string> ComputeInputFingerprintAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var input = await PrepareInputAsync(cancellationToken).ConfigureAwait(false);
                return input?.Fingerprint ?? string.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return $"fingerprint-error:{e.GetType().FullName}:{e.Message}";
            }
        }

        private async Task ExportFilesAsync(string oldFile, string newFile, CancellationToken cancellationToken)
        {
            await SaveContentSourceAsync(_option.OldContent, oldFile, cancellationToken).ConfigureAwait(false);
            await SaveContentSourceAsync(_option.NewContent, newFile, cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveContentSourceAsync(Models.DiffContentSource source, string saveTo, CancellationToken cancellationToken)
        {
            if (source.IsWorktree)
                await SaveWorktreeFileAsync(source.Path, saveTo, cancellationToken).ConfigureAwait(false);
            else
                await SaveRevisionAsync(source.Revision, source.Path, saveTo, cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveWorktreeFileAsync(string file, string saveTo, CancellationToken cancellationToken)
        {
            if (IsNullPath(file))
            {
                await File.WriteAllBytesAsync(saveTo, [], cancellationToken).ConfigureAwait(false);
                return;
            }

            var fullPath = Native.OS.GetAbsPath(_repo, file);
            var dir = Path.GetDirectoryName(saveTo);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(fullPath))
            {
                await using var src = File.OpenRead(fullPath);
                await using var dst = File.Create(saveTo);
                await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
            }
            else
                await File.WriteAllBytesAsync(saveTo, [], cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveRevisionAsync(string revision, string file, string saveTo, CancellationToken cancellationToken)
        {
            if (RevisionIncludesPath(revision))
            {
                if (TrySplitIndexRevisionSpec(revision, out var indexRevision, out var indexPath))
                    await SaveRevisionFile.RunAsync(_repo, indexRevision, indexPath, saveTo, cancellationToken).ConfigureAwait(false);
                else
                    await SaveRevisionSpecAsync(revision, saveTo, cancellationToken).ConfigureAwait(false);
            }
            else if (IsNullPath(file))
            {
                await File.WriteAllBytesAsync(saveTo, [], cancellationToken).ConfigureAwait(false);
                return;
            }
            else
                await SaveRevisionFile.RunAsync(_repo, revision, file, saveTo, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(saveTo))
                await File.WriteAllBytesAsync(saveTo, [], cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveRevisionSpecAsync(string revisionSpec, string saveTo, CancellationToken cancellationToken)
        {
            var dir = Path.GetDirectoryName(saveTo);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using var output = File.Create(saveTo);

            var start = new ProcessStartInfo
            {
                FileName = Native.OS.GitExecutable,
                Arguments = $"--no-pager -c core.quotepath=off show {revisionSpec}",
                WorkingDirectory = _repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            try
            {
                using var proc = Process.Start(start)!;
                using var cancellation = Command.RegisterProcessCancellation(cancellationToken, proc);
                var stderrTask = proc.StandardError.ReadToEndAsync();
                await proc.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (proc.ExitCode != 0)
                    output.SetLength(0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                output.SetLength(0);
                throw;
            }
            catch
            {
                output.SetLength(0);
            }
        }

        private async Task<Command.Result> RunRendererAsync(string oldFile, string newFile, string tempDir, CancellationToken cancellationToken)
        {
            var args = ExpandArguments(_renderer.Arguments, oldFile, newFile);
            var mode = GetCustomDiffMode();
            var baseRevision = GetRevisionOrEmpty(0);
            var targetRevision = GetRevisionOrEmpty(1);
            var commitRevision = _option.Context == Models.DiffOptionContext.Commit ? targetRevision : string.Empty;
            var start = new ProcessStartInfo
            {
                WorkingDirectory = _repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (OperatingSystem.IsWindows() && IsBatchFile(_renderer.Executable))
            {
                start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                start.Arguments = $"/d /c \"\"{_renderer.Executable}\" {args}\"";
            }
            else
            {
                start.FileName = _renderer.Executable;
                start.Arguments = args;
            }

            start.Environment["SOURCEGIT_CUSTOM_DIFF_TEMP"] = tempDir;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_OLD"] = oldFile;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_NEW"] = newFile;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_PATH"] = _option.Path;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_REPO"] = _repo;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_TITLE"] = _title;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_CONTEXT"] = _option.Context.ToString();
            start.Environment["SOURCEGIT_CUSTOM_DIFF_MODE"] = mode;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_BASE"] = baseRevision;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_TARGET"] = targetRevision;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_COMMIT"] = commitRevision;
            start.Environment["SOURCEGIT_CUSTOM_DIFF_IS_LOCAL"] = _option.IsLocalChange ? "1" : "0";
            start.Environment["SOURCEGIT_CUSTOM_DIFF_IS_UNSTAGED"] = _option.IsUnstaged ? "1" : "0";

            using var proc = new Process { StartInfo = start };
            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Command.Result.Failed(e.Message);
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var waitTask = proc.WaitForExitAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(RendererTimeoutSeconds));
            var cancelTask = cancellationToken.CanBeCanceled ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken) : null;
            using var cancelRegistration = Command.RegisterProcessCancellation(cancellationToken, proc);

            var completedTask = cancelTask == null ?
                await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false) :
                await Task.WhenAny(waitTask, timeoutTask, cancelTask).ConfigureAwait(false);

            if (completedTask == cancelTask || cancellationToken.IsCancellationRequested)
            {
                Command.QueueKillProcessTree(proc);
                await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(RendererKillGraceSeconds))).ConfigureAwait(false);

                if (!waitTask.IsCompleted)
                    _ = ObserveProcessShutdownAsync(waitTask, stdoutTask, stderrTask);

                throw new OperationCanceledException(cancellationToken);
            }

            if (completedTask != waitTask)
            {
                Command.QueueKillProcessTree(proc);
                await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(RendererKillGraceSeconds))).ConfigureAwait(false);

                var timeoutStdout = ReadProcessOutputIfCompleted(stdoutTask);
                var timeoutStderr = ReadProcessOutputIfCompleted(stderrTask);
                if (!string.IsNullOrWhiteSpace(timeoutStderr))
                    timeoutStderr = timeoutStderr.TrimEnd() + Environment.NewLine;

                if (!waitTask.IsCompleted)
                    _ = ObserveProcessShutdownAsync(waitTask, stdoutTask, stderrTask);

                return new Command.Result
                {
                    IsSuccess = false,
                    StdOut = timeoutStdout,
                    StdErr = $"{timeoutStderr}Custom diff renderer timed out after {RendererTimeoutSeconds} seconds.",
                };
            }

            await waitTask.ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new Command.Result
            {
                IsSuccess = proc.ExitCode == 0,
                StdOut = stdout,
                StdErr = stderr,
            };
        }

        private async Task<object> ParseOutputAsync(string stdout, string tempDir)
        {
            var html = ExtractHtml(stdout);
            if (!string.IsNullOrWhiteSpace(html))
            {
                var output = Path.Combine(tempDir, "renderer.html");
                await File.WriteAllTextAsync(output, html, Encoding.UTF8).ConfigureAwait(false);
                return new Models.HtmlDiff { Source = new Uri(output) };
            }

            var path = FindHtmlPath(stdout, tempDir);
            if (!string.IsNullOrEmpty(path))
            {
                if (!IsPathUnderDirectory(path, tempDir))
                    path = CopyHtmlFileToTemp(path, tempDir);

                return new Models.HtmlDiff { Source = new Uri(path) };
            }

            return new Models.CustomDiffEmpty
            {
                RendererName = _renderer.Name,
                Message = "The custom diff renderer did not output HTML or an HTML file path.",
            };
        }

        private string ExpandArguments(string arguments, string oldFile, string newFile)
        {
            var args = string.IsNullOrWhiteSpace(arguments) ? "\"$OLD\" \"$NEW\"" : arguments;
            var repoPath = OperatingSystem.IsWindows() ? _repo.Replace("/", "\\") : _repo;
            var baseRevision = GetRevisionOrEmpty(0);
            var targetRevision = GetRevisionOrEmpty(1);
            var commitRevision = _option.Context == Models.DiffOptionContext.Commit ? targetRevision : string.Empty;

            return args
                .Replace("$OLD", oldFile, StringComparison.Ordinal)
                .Replace("$NEW", newFile, StringComparison.Ordinal)
                .Replace("$LOCAL", newFile, StringComparison.Ordinal)
                .Replace("$REMOTE", oldFile, StringComparison.Ordinal)
                .Replace("$PATH", _option.Path, StringComparison.Ordinal)
                .Replace("$REPO", repoPath, StringComparison.Ordinal)
                .Replace("$TITLE", _title, StringComparison.Ordinal)
                .Replace("$CONTEXT", _option.Context.ToString(), StringComparison.Ordinal)
                .Replace("$MODE", GetCustomDiffMode(), StringComparison.Ordinal)
                .Replace("$BASE", baseRevision, StringComparison.Ordinal)
                .Replace("$TARGET", targetRevision, StringComparison.Ordinal)
                .Replace("$COMMIT", commitRevision, StringComparison.Ordinal);
        }

        private object Error(string message)
        {
            return new Models.CustomDiffError
            {
                RendererName = _renderer.Name,
                Message = message,
            };
        }

        private string GetTempExtension()
        {
            var ext = Path.GetExtension(_option.Path);
            return string.IsNullOrEmpty(ext) ? ".tmp" : ext;
        }

        private string GetRevisionOrEmpty(int index)
        {
            return index >= 0 && index < _option.Revisions.Count ? _option.Revisions[index] : string.Empty;
        }

        private string GetCustomDiffMode()
        {
            return _option.Context switch
            {
                Models.DiffOptionContext.WorkingCopy => _option.IsUnstaged ? "local-unstaged" : "local-staged",
                Models.DiffOptionContext.Commit => "commit",
                Models.DiffOptionContext.FileHistory => "file-history",
                Models.DiffOptionContext.FileHistoryCompare => "file-history-compare",
                Models.DiffOptionContext.RevisionCompare => "revision-compare",
                Models.DiffOptionContext.Stash => _option.CustomDiffMode,
                _ => "unknown",
            };
        }

        private static string ExtractHtml(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout))
                return string.Empty;

            var doctype = stdout.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
            var html = stdout.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            var start = doctype >= 0 && html >= 0 ? Math.Min(doctype, html) : Math.Max(doctype, html);
            return start >= 0 ? stdout.Substring(start).Trim() : string.Empty;
        }

        private string FindHtmlPath(string stdout, string tempDir)
        {
            if (string.IsNullOrWhiteSpace(stdout))
                return string.Empty;

            var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var candidate = lines[i].Trim().Trim('"');
                if (!candidate.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fullpath = ResolveHtmlPath(candidate, tempDir);
                if (!string.IsNullOrEmpty(fullpath))
                    return fullpath;
            }

            return string.Empty;
        }

        private string ResolveHtmlPath(string path, string tempDir)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
                path = uri.LocalPath;

            if (Path.IsPathRooted(path))
                return File.Exists(path) ? Path.GetFullPath(path) : string.Empty;

            var tempPath = Path.GetFullPath(Path.Combine(tempDir, path));
            if (File.Exists(tempPath))
                return tempPath;

            var repoPath = Path.GetFullPath(Path.Combine(_repo, path));
            return File.Exists(repoPath) ? repoPath : string.Empty;
        }

        private static bool RevisionIncludesPath(string revision)
        {
            return !revision.Equals(":0", StringComparison.Ordinal) && revision.Contains(':', StringComparison.Ordinal);
        }

        private static bool TrySplitIndexRevisionSpec(string revisionSpec, out string revision, out string path)
        {
            revision = string.Empty;
            path = string.Empty;

            var spec = revisionSpec;
            if (spec.Length >= 2 && spec[0] == '"' && spec[^1] == '"')
                spec = spec[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);

            if (spec.Length < 4 ||
                spec[0] != ':' ||
                spec[2] != ':' ||
                spec[1] < '0' ||
                spec[1] > '3')
                return false;

            revision = spec[..2];
            path = spec[3..];
            return !string.IsNullOrEmpty(path);
        }

        private static bool IsNullPath(string path)
        {
            return string.IsNullOrEmpty(path) || path.Equals("/dev/null", StringComparison.Ordinal);
        }

        private static bool IsBatchFile(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldKeepTempDir(object parsed, string tempDir)
        {
            if (parsed is not Models.HtmlDiff { Source: { IsFile: true } source })
                return false;

            return IsPathUnderDirectory(source.LocalPath, tempDir);
        }

        private static string CopyHtmlFileToTemp(string htmlPath, string tempDir)
        {
            var outputDir = Path.Combine(tempDir, "renderer-file");
            Directory.CreateDirectory(outputDir);
            var output = Path.Combine(outputDir, Path.GetFileName(htmlPath));
            File.Copy(htmlPath, output, true);
            return output;
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            var fullpath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullpath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static void CleanupStaleTempDirs(string tempRoot)
        {
            if (!Directory.Exists(tempRoot))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var dir in Directory.EnumerateDirectories(tempRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, true);
                }
                catch
                {
                    // Ignore directories still used by an active WebView or renderer.
                }
            }
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
                // Ignore files still held by the renderer while the timeout cleanup settles.
            }
        }

        private static string ReadProcessOutputIfCompleted(Task<string> outputTask)
        {
            if (!outputTask.IsCompleted)
                return string.Empty;

            try
            {
                return outputTask.GetAwaiter().GetResult();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
        {
            if (!File.Exists(path))
                return "missing";

            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }

        private static async Task ObserveProcessShutdownAsync(Task waitTask, params Task<string>[] outputTasks)
        {
            try
            {
                await waitTask.ConfigureAwait(false);
            }
            catch
            {
                // The UI has already received the timeout error.
            }

            foreach (var outputTask in outputTasks)
            {
                try
                {
                    await outputTask.ConfigureAwait(false);
                }
                catch
                {
                    // Drain task exceptions from late process shutdown.
                }
            }
        }

        private readonly string _repo;
        private readonly Models.DiffOption _option;
        private readonly Models.CustomDiffRenderer _renderer;
        private readonly string _title;
    }
}
