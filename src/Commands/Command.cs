using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Command
    {
        public class Result
        {
            public bool IsSuccess { get; set; } = false;
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;

            public static Result Failed(string reason) => new Result() { StdErr = reason };
        }

        public enum EditorType
        {
            None,
            CoreEditor,
            RebaseEditor,
        }

        public string Context { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = null;
        public EditorType Editor { get; set; } = EditorType.CoreEditor;
        public string SSHKey { get; set; } = string.Empty;
        public string Args { get; set; } = string.Empty;

        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        public bool CanAbortProcessOnCancel { get; set; } = false;
        // Keep abortable commands read-only in partial clones unless callers must allow blob lazy-fetch.
        public bool DisableLazyFetchOnAbort { get; set; } = true;
        public bool RaiseError { get; set; } = true;
        public Models.ICommandLog Log { get; set; } = null;

        public async Task<bool> ExecAsync()
        {
            using var span = StartGitDiagnosticSpan("exec");
            Log?.AppendLine($"$ git {Args}\n");

            var errs = new List<string>();

            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);
            proc.OutputDataReceived += (_, e) => HandleOutput(e.Data, errs);
            proc.ErrorDataReceived += (_, e) => HandleOutput(e.Data, errs);

            CancellationTokenRegistration cancellation = default;
            try
            {
                proc.Start();
                cancellation = RegisterProcessCancellation(proc);
            }
            catch (Exception e)
            {
                span.Set("startError", e.Message);
                span.Set("success", false);
                if (RaiseError)
                    RaiseException(e.Message);

                Log?.AppendLine(string.Empty);
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                HandleOutput(e.Message, errs);
            }
            finally
            {
                cancellation.Dispose();
            }

            Log?.AppendLine(string.Empty);
            var hasExited = false;
            try
            {
                hasExited = proc.HasExited;
                if (hasExited)
                    span.Set("exitCode", proc.ExitCode);
            }
            catch
            {
                // Ignore races with process disposal/abort.
            }

            span.Set("canceled", CancellationToken.IsCancellationRequested);
            span.Set("stderrLines", errs.Count);

            if (CancellationToken.IsCancellationRequested)
                return false;

            if (!hasExited || proc.ExitCode != 0)
            {
                span.Set("success", false);
                if (RaiseError)
                {
                    var errMsg = string.Join("\n", errs).Trim();
                    if (!string.IsNullOrEmpty(errMsg))
                        RaiseException(errMsg);
                }

                return false;
            }

            span.Set("success", true);
            return true;
        }

        protected Result ReadToEnd()
        {
            using var span = StartGitDiagnosticSpan("read");
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                span.Set("startError", e.Message);
                span.Set("success", false);
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            rs.StdOut = proc.StandardOutput.ReadToEnd();
            rs.StdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            rs.IsSuccess = proc.ExitCode == 0;
            span.Set("exitCode", proc.ExitCode);
            span.Set("success", rs.IsSuccess);
            span.Set("stdoutBytes", Encoding.UTF8.GetByteCount(rs.StdOut));
            span.Set("stderrBytes", Encoding.UTF8.GetByteCount(rs.StdErr));
            return rs;
        }

        protected async Task<Result> ReadToEndAsync()
        {
            using var span = StartGitDiagnosticSpan("read_async");
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                span.Set("startError", e.Message);
                span.Set("success", false);
                return Result.Failed(e.Message);
            }

            using var cancellation = RegisterProcessCancellation(proc);
            var rs = new Result() { IsSuccess = true };
            try
            {
                rs.StdOut = await proc.StandardOutput.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
                rs.StdErr = await proc.StandardError.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                span.Set("canceled", true);
                span.Set("success", false);
                return Result.Failed("Cancelled");
            }

            rs.IsSuccess = !CancellationToken.IsCancellationRequested && proc.ExitCode == 0;
            span.Set("exitCode", proc.ExitCode);
            span.Set("canceled", CancellationToken.IsCancellationRequested);
            span.Set("success", rs.IsSuccess);
            span.Set("stdoutBytes", Encoding.UTF8.GetByteCount(rs.StdOut));
            span.Set("stderrBytes", Encoding.UTF8.GetByteCount(rs.StdErr));
            return rs;
        }

        protected CancellationTokenRegistration RegisterProcessCancellation(Process proc)
        {
            if (!CanAbortProcessOnCancel || !CancellationToken.CanBeCanceled)
                return default;

            return RegisterProcessCancellation(CancellationToken, proc);
        }

        internal static CancellationTokenRegistration RegisterProcessCancellation(CancellationToken cancellationToken, Process proc)
        {
            if (!cancellationToken.CanBeCanceled)
                return default;

            var target = CaptureProcessKillTarget(proc);
            if (!target.IsValid)
                return default;

            return cancellationToken.Register(static state =>
            {
                if (state is ProcessKillTarget target)
                    QueueKillProcessTree(target);
            }, target, useSynchronizationContext: false);
        }

        internal static void QueueKillProcessTree(Process proc)
        {
            var target = CaptureProcessKillTarget(proc);
            if (target.IsValid)
                QueueKillProcessTree(target);
        }

        private static ProcessKillTarget CaptureProcessKillTarget(Process proc)
        {
            try
            {
                var processId = proc.Id;
                var startTime = proc.StartTime;
                return new ProcessKillTarget(processId, startTime);
            }
            catch
            {
                return default;
            }
        }

        private static void QueueKillProcessTree(ProcessKillTarget target)
        {
            _ = Task.Run(() => KillProcessTree(target));
        }

        private static void KillProcessTree(ProcessKillTarget target)
        {
            try
            {
                using var proc = Process.GetProcessById(target.ProcessId);
                try
                {
                    if (proc.StartTime != target.StartTime)
                        return;
                }
                catch
                {
                    return;
                }

                if (!proc.HasExited)
                    proc.Kill(true);
            }
            catch
            {
                // Ignore cancellation races with normal process exit.
            }
        }

        protected ProcessStartInfo CreateGitStartInfo(bool redirect)
        {
            var start = new ProcessStartInfo();
            start.FileName = Native.OS.GitExecutable;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;

            if (redirect)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
            }

            // Force using this app as SSH askpass program
            var selfExecFile = Environment.ProcessPath;
            start.Environment.Add("SSH_ASKPASS", selfExecFile); // Can not use parameter here, because it invoked by SSH with `exec`
            start.Environment.Add("SSH_ASKPASS_REQUIRE", "prefer");
            start.Environment.Add("SOURCEGIT_LAUNCH_AS_ASKPASS", "TRUE");
            if (!OperatingSystem.IsLinux())
                start.Environment.Add("DISPLAY", "required");

            // If an SSH private key was provided, sets the environment.
            if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !string.IsNullOrEmpty(SSHKey))
                start.Environment.Add("GIT_SSH_COMMAND", $"ssh -i '{SSHKey}' -F '/dev/null'");

            // Abortable commands must remain read-only even in partial clones.
            if (CanAbortProcessOnCancel && DisableLazyFetchOnAbort)
                start.Environment.Add("GIT_NO_LAZY_FETCH", "1");

            // Force using en_US.UTF-8 locale
            if (OperatingSystem.IsLinux())
            {
                start.Environment.Add("LANG", "C");
                start.Environment.Add("LC_ALL", "C");
            }

            var builder = new StringBuilder(2048);
            builder
                .Append("--no-pager -c core.quotepath=off ");
            GitRuntimeConfig.Append(builder, WorkingDirectory ?? Context);
            builder
                .Append("-c credential.helper=")
                .Append(Native.OS.CredentialHelper)
                .Append(' ');

            switch (Editor)
            {
                case EditorType.CoreEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --core-editor" """);
                    break;
                case EditorType.RebaseEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --rebase-message-editor" -c sequence.editor="\"{selfExecFile}\" --rebase-todo-editor" -c rebase.abbreviateCommands=true """);
                    break;
                default:
                    builder.Append("-c core.editor=true ");
                    break;
            }

            builder.Append(Args);
            start.Arguments = builder.ToString();

            // Working directory
            if (!string.IsNullOrEmpty(WorkingDirectory))
                start.WorkingDirectory = WorkingDirectory;

            return start;
        }

        protected void RaiseException(string error)
        {
            Models.Notification.Send(Context, error, true);
        }

        private protected SourceGit.Diagnostics.DiagnosticScope StartGitDiagnosticSpan(string mode)
        {
            var repoPath = SourceGit.Diagnostics.DiagnosticManager.GetRepositoryPath(WorkingDirectory ?? Context);
            return SourceGit.Diagnostics.DiagnosticManager.StartSpan(
                "Git.Command",
                $"git.{mode}",
                SourceGit.Diagnostics.DiagnosticManager.CreateData(
                    ("mode", mode),
                    ("repo", SourceGit.Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("args", SourceGit.Diagnostics.DiagnosticManager.Redact(Args))));
        }

        private void HandleOutput(string line, List<string> errs)
        {
            if (line == null)
                return;

            Log?.AppendLine(line);

            // Lines to hide in error message.
            if (line.Length > 0)
            {
                if (line.StartsWith("remote: Enumerating objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Counting objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Compressing objects:", StringComparison.Ordinal) ||
                    line.StartsWith("Filtering content:", StringComparison.Ordinal) ||
                    line.StartsWith("hint:", StringComparison.Ordinal))
                    return;

                if (REG_PROGRESS().IsMatch(line))
                    return;
            }

            errs.Add(line);
        }

        private readonly struct ProcessKillTarget
        {
            public ProcessKillTarget(int processId, DateTime startTime)
            {
                ProcessId = processId;
                StartTime = startTime;
            }

            public int ProcessId { get; }
            public DateTime StartTime { get; }
            public bool IsValid => ProcessId > 0;
        }

        [GeneratedRegex(@"\d+%")]
        private static partial Regex REG_PROGRESS();
    }
}
