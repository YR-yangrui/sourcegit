using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryRevisionFileNames : Command
    {
        public QueryRevisionFileNames(string repo, string revision)
        {
            WorkingDirectory = repo;
            Context = repo;
            CanAbortProcessOnCancel = true;
            Args = $"ls-tree -r --name-only {revision}";
        }

        public async Task<List<string>> GetResultAsync()
        {
            using var span = StartGitDiagnosticSpan("query_revision_file_names");
            var outs = new List<string>();

            try
            {
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                using var cancellation = RegisterProcessCancellation(proc);
                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { Length: > 0 } line)
                    outs.Add(line);

                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
                span.Set("exitCode", proc.ExitCode);
                span.Set("fileCount", outs.Count);
                span.Set("canceled", CancellationToken.IsCancellationRequested);
                span.Set("success", !CancellationToken.IsCancellationRequested && proc.ExitCode == 0);
            }
            catch (Exception e)
            {
                span.Set("exception", e.GetType().Name);
                span.Set("fileCount", outs.Count);
                span.Set("canceled", CancellationToken.IsCancellationRequested);
                span.Set("success", false);
                if (CancellationToken.IsCancellationRequested)
                    return [];

                // Ignore exceptions.
            }

            return CancellationToken.IsCancellationRequested ? [] : outs;
        }
    }
}
