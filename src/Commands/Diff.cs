using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Diff : Command
    {
        public Diff(string repo, Models.DiffOption opt, int unified, bool ignoreWhitespace, bool ignoreCRAtEOL)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder(256);
            builder.Append("diff --no-color --no-ext-diff --full-index --patch ");
            if (ignoreWhitespace)
                builder.Append("--ignore-space-change ");
            if (ignoreCRAtEOL)
                builder.Append("--ignore-cr-at-eol ");
            builder.Append("--unified=").Append(unified).Append(' ');
            builder.Append(opt.ToString());

            Args = builder.ToString();
        }

        public async Task<Models.DiffResult> ReadAsync()
        {
            using var span = StartGitDiagnosticSpan("diff");
            try
            {
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                using var cancellation = RegisterProcessCancellation(proc);
                using var ms = new MemoryStream();
                await proc.StandardOutput.BaseStream.CopyToAsync(ms, CancellationToken).ConfigureAwait(false);
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
                span.Set("exitCode", proc.ExitCode);
                span.Set("canceled", CancellationToken.IsCancellationRequested);
                span.Set("stdoutBytes", ms.Length);

                if (!CancellationToken.IsCancellationRequested)
                {
                    span.Set("success", proc.ExitCode == 0);
                    return DiffParser.Parse(ms.ToArray());
                }
            }
            catch (System.Exception e)
            {
                span.Set("success", false);
                span.Set("error", e.Message);
                // Ignore exceptions.
            }

            span.Set("success", false);
            return DiffParser.Parse([]);
        }
    }
}
