using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCommits : Command
    {
        public QueryCommits(string repo, string limits, bool markMerged = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"log --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s {limits}";
            _markMerged = markMerged;
        }

        public QueryCommits(string repo, string filter, Models.CommitSearchMethod method, Models.HistoryQueryScope scope)
        {
            var builder = new StringBuilder();
            builder.Append("log -1000 --date-order --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s ");

            if (scope?.Kind == Models.HistoryQueryScopeKind.AllBranches)
            {
                builder.Append("--branches --remotes ");
            }
            else if (!string.IsNullOrEmpty(scope?.RevisionArg))
            {
                builder.Append(scope.RevisionArg.Quoted()).Append(' ');
            }

            if (method == Models.CommitSearchMethod.ByAuthor)
            {
                builder.Append("-i --author=").Append(filter.Quoted());
            }
            else if (method == Models.CommitSearchMethod.ByCommitter)
            {
                builder.Append("-i --committer=").Append(filter.Quoted());
            }
            else if (method == Models.CommitSearchMethod.ByMessage)
            {
                var words = filter.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                    builder.Append("--grep=").Append(word.Trim().Quoted()).Append(' ');
                builder.Append("--all-match -i");
            }
            else if (method == Models.CommitSearchMethod.ByPath)
            {
                builder.Append("--follow -- ").Append(filter.Quoted());
            }
            else
            {
                builder.Append("-G").Append(filter.Quoted());
            }

            WorkingDirectory = repo;
            Context = repo;
            Args = builder.ToString();
            _markMerged = false;
        }

        public async Task<List<Models.Commit>> GetResultAsync()
        {
            var result = await GetResultWithStatusAsync().ConfigureAwait(false);
            return result.Commits;
        }

        public async Task<QueryResult> GetResultWithStatusAsync()
        {
            var commits = new List<Models.Commit>();
            var success = false;
            using var span = StartGitDiagnosticSpan("query_commits");
            try
            {
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                var findHead = false;
                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var parts = line.Split('\0');
                    if (parts.Length != 8)
                        continue;

                    var commit = new Models.Commit() { SHA = parts[0] };
                    commit.ParseParents(parts[1]);
                    commit.ParseDecorators(parts[2]);
                    commit.Author = Models.User.FindOrAdd(parts[3]);
                    commit.AuthorTime = ulong.Parse(parts[4]);
                    commit.Committer = Models.User.FindOrAdd(parts[5]);
                    commit.CommitterTime = ulong.Parse(parts[6]);
                    commit.Subject = parts[7];
                    commits.Add(commit);

                    if (!findHead && commit.IsMerged)
                        findHead = true;
                }

                await proc.WaitForExitAsync().ConfigureAwait(false);
                success = proc.ExitCode == 0;
                span.Set("exitCode", proc.ExitCode);
                span.Set("success", success);
                span.Set("commitCount", commits.Count);

                if (success && _markMerged && !findHead && commits.Count > 0)
                {
                    var set = await new QueryCurrentBranchCommitHashes(WorkingDirectory, commits[^1].CommitterTime)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    foreach (var c in commits)
                    {
                        if (set.Contains(c.SHA))
                        {
                            c.IsMerged = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                span.Set("success", false);
                span.Set("error", e.Message);
                RaiseException($"Failed to query commits. Reason: {e.Message}");
            }

            return new QueryResult()
            {
                IsSuccess = success,
                Commits = commits,
            };
        }

        public class QueryResult
        {
            public bool IsSuccess { get; set; }
            public List<Models.Commit> Commits { get; set; } = [];
        }

        private bool _markMerged = false;
    }
}
