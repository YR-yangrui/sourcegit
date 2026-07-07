using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class QueryFileContent
    {
        public static async Task<Stream> RunAsync(string repo, string revision, string file, CancellationToken cancellationToken = default)
        {
            var starter = new ProcessStartInfo();
            starter.WorkingDirectory = repo;
            starter.FileName = Native.OS.GitExecutable;

            var builder = new StringBuilder();
            GitRuntimeConfig.Append(builder, repo);
            builder.Append($"show {revision}:{file.Quoted()}");
            starter.Arguments = builder.ToString();

            starter.UseShellExecute = false;
            starter.CreateNoWindow = true;
            starter.WindowStyle = ProcessWindowStyle.Hidden;
            starter.RedirectStandardOutput = true;

            var stream = new MemoryStream();
            try
            {
                using var proc = Process.Start(starter)!;
                using var cancellation = Command.RegisterProcessCancellation(cancellationToken, proc);

                await proc.StandardOutput.BaseStream.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Models.Notification.Send(repo, $"Failed to query file content: {e}", true);
            }

            stream.Position = 0;
            return stream;
        }

        public static async Task<byte[]> RunSpecAsBytesAsync(string repo, string spec)
        {
            var starter = new ProcessStartInfo();
            starter.WorkingDirectory = repo;
            starter.FileName = Native.OS.GitExecutable;

            var builder = new StringBuilder();
            GitRuntimeConfig.Append(builder, repo);
            // Structured diff needs exact Git object specs such as ":path" or "REV:path";
            // quote the whole spec so paths containing spaces stay a single argument.
            builder.Append("show ").Append(spec.Quoted());
            starter.Arguments = builder.ToString();

            starter.UseShellExecute = false;
            starter.CreateNoWindow = true;
            starter.WindowStyle = ProcessWindowStyle.Hidden;
            starter.RedirectStandardOutput = true;
            starter.RedirectStandardError = true;

            var stream = new MemoryStream();
            try
            {
                using var proc = Process.Start(starter)!;
                await proc.StandardOutput.BaseStream.CopyToAsync(stream).ConfigureAwait(false);
                await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                if (proc.ExitCode != 0)
                    return null;
            }
            catch
            {
                return null;
            }

            return stream.ToArray();
        }

        public static async Task<Stream> FromLFSAsync(string repo, string oid, long size)
        {
            var starter = new ProcessStartInfo();
            starter.WorkingDirectory = repo;
            starter.FileName = Native.OS.GitExecutable;

            var builder = new StringBuilder();
            GitRuntimeConfig.Append(builder, repo);
            builder.Append("lfs smudge");
            starter.Arguments = builder.ToString();

            starter.UseShellExecute = false;
            starter.CreateNoWindow = true;
            starter.WindowStyle = ProcessWindowStyle.Hidden;
            starter.RedirectStandardInput = true;
            starter.RedirectStandardOutput = true;

            var stream = new MemoryStream();
            try
            {
                using var proc = Process.Start(starter)!;
                await proc.StandardInput.WriteLineAsync("version https://git-lfs.github.com/spec/v1").ConfigureAwait(false);
                await proc.StandardInput.WriteLineAsync($"oid sha256:{oid}").ConfigureAwait(false);
                await proc.StandardInput.WriteLineAsync($"size {size}").ConfigureAwait(false);
                await proc.StandardOutput.BaseStream.CopyToAsync(stream).ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Models.Notification.Send(repo, $"Failed to query file content: {e}", true);
            }

            stream.Position = 0;
            return stream;
        }
    }
}
