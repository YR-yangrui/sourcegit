using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class SaveRevisionFile
    {
        public static async Task<string> GetWorktreePathIfRevisionMatchesDiskAsync(string repo, string revision, string file, string revisionBlobSHA = null)
        {
            if (string.IsNullOrEmpty(file))
                return null;

            var targetBlobSHA = revisionBlobSHA;
            if (string.IsNullOrEmpty(targetBlobSHA))
            {
                targetBlobSHA = await new QueryRevisionFileObjectSHA(repo, revision, file)
                    .GetResultAsync()
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(targetBlobSHA))
                return null;

            var fullPath = Native.OS.GetAbsPath(repo, file);
            if (!File.Exists(fullPath))
                return null;

            var matched = await DoesFileMatchGitBlobHashAsync(fullPath, targetBlobSHA)
                .ConfigureAwait(false);
            return matched ? fullPath : null;
        }

        public static async Task RunAsync(string repo, string revision, string file, string saveTo, CancellationToken cancellationToken = default)
        {
            var dir = Path.GetDirectoryName(saveTo) ?? string.Empty;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var isLFSFiltered = await new IsLFSFiltered(repo, revision, file)
            {
                CancellationToken = cancellationToken,
                CanAbortProcessOnCancel = true,
            }.GetResultAsync().ConfigureAwait(false);
            if (isLFSFiltered)
            {
                var pointerStream = await QueryFileContent.RunAsync(repo, revision, file, cancellationToken).ConfigureAwait(false);
                await ExecCmdAsync(repo, "lfs smudge", saveTo, pointerStream, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ExecCmdAsync(repo, $"show {revision}:{file.Quoted()}", saveTo, null, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task ExecCmdAsync(string repo, string args, string outputFile, Stream input = null, CancellationToken cancellationToken = default)
        {
            var starter = new ProcessStartInfo();
            starter.WorkingDirectory = repo;
            starter.FileName = Native.OS.GitExecutable;
            var builder = new StringBuilder();
            GitRuntimeConfig.Append(builder, repo);
            builder.Append(args);
            starter.Arguments = builder.ToString();
            starter.UseShellExecute = false;
            starter.CreateNoWindow = true;
            starter.WindowStyle = ProcessWindowStyle.Hidden;
            starter.RedirectStandardInput = true;
            starter.RedirectStandardOutput = true;
            starter.RedirectStandardError = true;

            await using (var sw = File.Create(outputFile))
            {
                try
                {
                    using var proc = Process.Start(starter)!;
                    using var cancellation = Command.RegisterProcessCancellation(cancellationToken, proc);

                    if (input != null)
                    {
                        var inputString = await new StreamReader(input).ReadToEndAsync().ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        await proc.StandardInput.WriteAsync(inputString).ConfigureAwait(false);
                    }

                    await proc.StandardOutput.BaseStream.CopyToAsync(sw, cancellationToken).ConfigureAwait(false);
                    await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Models.Notification.Send(repo, "Save file failed: " + e.Message, true);
                }
            }
        }

        private static async Task<bool> DoesFileMatchGitBlobHashAsync(string file, string expected)
        {
            var raw = await CalcGitBlobHashAsync(file, expected.Length).ConfigureAwait(false);
            if (expected.Equals(raw, StringComparison.OrdinalIgnoreCase))
                return true;

            var lf = await CalcGitBlobHashAsync(file, expected.Length, LineEndingMode.LF).ConfigureAwait(false);
            if (expected.Equals(lf, StringComparison.OrdinalIgnoreCase))
                return true;

            var crlf = await CalcGitBlobHashAsync(file, expected.Length, LineEndingMode.CRLF).ConfigureAwait(false);
            return expected.Equals(crlf, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> CalcGitBlobHashAsync(string file, int objectHashLength, LineEndingMode lineEndingMode = LineEndingMode.None)
        {
            var algorithm = objectHashLength switch
            {
                40 => HashAlgorithmName.SHA1,
                64 => HashAlgorithmName.SHA256,
                _ => default,
            };

            if (string.IsNullOrEmpty(algorithm.Name))
                return null;

            try
            {
                if (lineEndingMode == LineEndingMode.None)
                {
                    var fileInfo = new FileInfo(file);
                    using var hasher = IncrementalHash.CreateHash(algorithm);
                    var header = Encoding.ASCII.GetBytes($"blob {fileInfo.Length}\0");
                    hasher.AppendData(header);

                    await using var stream = File.OpenRead(file);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        hasher.AppendData(buffer, 0, read);

                    return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                }

                var normalized = NormalizeLineEndings(await File.ReadAllBytesAsync(file).ConfigureAwait(false), lineEndingMode);
                return CalcGitBlobHash(normalized, algorithm);
            }
            catch
            {
                return null;
            }
        }

        private static string CalcGitBlobHash(byte[] content, HashAlgorithmName algorithm)
        {
            using var hasher = IncrementalHash.CreateHash(algorithm);
            var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
            hasher.AppendData(header);
            hasher.AppendData(content);
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        private static byte[] NormalizeLineEndings(byte[] content, LineEndingMode mode)
        {
            using var stream = new MemoryStream(content.Length);
            for (var i = 0; i < content.Length; i++)
            {
                var b = content[i];
                if (b == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    if (mode == LineEndingMode.LF)
                    {
                        stream.WriteByte((byte)'\n');
                    }
                    else
                    {
                        stream.WriteByte((byte)'\r');
                        stream.WriteByte((byte)'\n');
                    }

                    i++;
                }
                else if (b == '\n' && mode == LineEndingMode.CRLF)
                {
                    stream.WriteByte((byte)'\r');
                    stream.WriteByte((byte)'\n');
                }
                else
                {
                    stream.WriteByte(b);
                }
            }

            return stream.ToArray();
        }

        private enum LineEndingMode
        {
            None,
            LF,
            CRLF,
        }
    }
}
