using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCurrentBranchCommitHashes : Command
    {
        public QueryCurrentBranchCommitHashes(string repo, ulong sinceTimestamp)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"log --since=@{sinceTimestamp} --format=%H";
        }

        public async Task<HashSet<string>> GetResultAsync()
        {
            var outs = new HashSet<string>();
            using var span = StartGitDiagnosticSpan("query_current_branch_commit_hashes");

            try
            {
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { Length: > 8 } line)
                    outs.Add(line);

                await proc.WaitForExitAsync().ConfigureAwait(false);
                span.Set("exitCode", proc.ExitCode);
                span.Set("success", proc.ExitCode == 0);
                span.Set("commitCount", outs.Count);
            }
            catch (System.Exception e)
            {
                span.Set("success", false);
                span.Set("error", e.Message);
                // Ignore exceptions;
            }

            return outs;
        }
    }
}
