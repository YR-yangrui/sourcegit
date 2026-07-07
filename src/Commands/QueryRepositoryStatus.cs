using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class QueryRepositoryStatus : Command
    {
        [GeneratedRegex(@"\+(\d+) \-(\d+)")]
        private static partial Regex REG_BRANCH_AB();

        public QueryRepositoryStatus(string repo, bool includeUntracked)
        {
            WorkingDirectory = repo;
            RaiseError = false;
            _includeUntracked = includeUntracked;
        }

        public async Task<Models.RepositoryStatus> GetResultAsync()
        {
            Args = $"{GetStatusUntrackedArg(_includeUntracked)} --porcelain=v2 -b";
            using var span = StartGitDiagnosticSpan("query_repository_status");
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            span.Set("statusSuccess", rs.IsSuccess);
            if (!rs.IsSuccess)
            {
                span.Set("success", false);
                return null;
            }

            var status = new Models.RepositoryStatus();
            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var count = lines.Length;
            span.Set("lineCount", count);
            if (count < 2)
            {
                span.Set("success", false);
                span.Set("error", "not_enough_status_lines");
                return null;
            }

            var sha1 = lines[0].Substring(13).Trim(); // Remove "# branch.oid " prefix
            var head = lines[1].Substring(14).Trim(); // Remove "# branch.head " prefix

            if (head.Equals("(detached)", StringComparison.Ordinal))
                status.CurrentBranch = sha1.Length > 10 ? $"({sha1.Substring(0, 10)})" : "-";
            else
                status.CurrentBranch = head;

            foreach (var line in lines)
            {
                if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    ParseTrackStatus(status, line.Substring(12).Trim());
                    break;
                }
            }

            status.LocalChanges = await new CountLocalChanges(WorkingDirectory, _includeUntracked) { RaiseError = false }
                .GetResultAsync()
                .ConfigureAwait(false);

            span.Set("success", true);
            span.Set("currentBranch", status.CurrentBranch);
            span.Set("localChanges", status.LocalChanges);
            span.Set("ahead", status.Ahead);
            span.Set("behind", status.Behind);
            return status;
        }

        private void ParseTrackStatus(Models.RepositoryStatus status, string input)
        {
            var match = REG_BRANCH_AB().Match(input);
            if (match.Success)
            {
                status.Ahead = int.Parse(match.Groups[1].Value);
                status.Behind = int.Parse(match.Groups[2].Value);
            }
        }

        private readonly bool _includeUntracked;
    }
}
