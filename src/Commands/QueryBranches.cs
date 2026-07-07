using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryBranches : Command
    {
        private const string PREFIX_LOCAL = "refs/heads/";
        private const string PREFIX_REMOTE = "refs/remotes/";
        private const string PREFIX_DETACHED_AT = "(HEAD detached at";
        private const string PREFIX_DETACHED_FROM = "(HEAD detached from";

        public QueryBranches(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = "branch -l --all -v --format=\"%(refname)%00%(committerdate:unix)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(upstream:trackshort)%00%(worktreepath)\"";
        }

        public async Task<List<Models.Branch>> GetResultAsync()
        {
            var branches = new List<Models.Branch>();
            using var span = StartGitDiagnosticSpan("query_branches");
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
            {
                span.Set("success", false);
                return branches;
            }

            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var mismatched = new HashSet<string>();
            var remotes = new Dictionary<string, Models.Branch>();
            span.Set("lineCount", lines.Length);
            foreach (var line in lines)
            {
                var b = ParseLine(line, mismatched);
                if (b != null)
                {
                    branches.Add(b);
                    if (!b.IsLocal)
                        remotes.Add(b.FullName, b);
                }
            }

            foreach (var b in branches)
            {
                if (b.IsLocal && !string.IsNullOrEmpty(b.Upstream))
                {
                    if (remotes.TryGetValue(b.Upstream, out var upstream))
                    {
                        b.IsUpstreamGone = false;

                        if (mismatched.Contains(b.FullName))
                            await new QueryTrackStatus(WorkingDirectory).GetResultAsync(b, upstream).ConfigureAwait(false);
                    }
                    else
                    {
                        b.IsUpstreamGone = true;
                    }
                }
            }

            span.Set("success", true);
            span.Set("branchCount", branches.Count);
            span.Set("remoteBranchCount", remotes.Count);
            span.Set("mismatchedTrackCount", mismatched.Count);
            return branches;
        }

        public async Task<Dictionary<string, Models.Branch>> GetDetailsByRefsAsync(IReadOnlyList<string> refs)
        {
            var result = await GetDetailsWithStatusByRefsAsync(refs).ConfigureAwait(false);
            return result.Branches;
        }

        public async Task<DetailsResult> GetDetailsWithStatusByRefsAsync(IReadOnlyList<string> refs)
        {
            var branches = new Dictionary<string, Models.Branch>(StringComparer.Ordinal);
            if (refs == null || refs.Count == 0)
                return new DetailsResult() { IsSuccess = true, Branches = branches };

            var chunks = BuildRefChunks(refs, "refs/heads refs/remotes");
            Args = $"for-each-ref --format=\"%(refname)%00%(committerdate:unix)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(worktreepath)\" <{refs.Count} refs>";
            using var span = StartGitDiagnosticSpan("query_branch_details");
            var success = true;
            var commandCount = 0;

            foreach (var chunk in chunks)
            {
                Args = $"for-each-ref --format=\"%(refname)%00%(committerdate:unix)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(worktreepath)\" {chunk}";
                commandCount++;

                var rs = await ReadToEndAsync().ConfigureAwait(false);
                if (!rs.IsSuccess)
                {
                    success = false;
                    continue;
                }

                var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var branch = ParseDetailLine(line);
                    if (branch != null)
                        branches[branch.FullName] = branch;
                }
            }

            span.Set("success", success);
            span.Set("requestedRefCount", refs.Count);
            span.Set("commandCount", commandCount);
            span.Set("branchCount", branches.Count);
            return new DetailsResult()
            {
                IsSuccess = success,
                Branches = branches,
            };
        }

        private Models.Branch ParseLine(string line, HashSet<string> mismatched)
        {
            var parts = line.Split('\0');
            if (parts.Length != 7)
                return null;

            var refName = parts[0];
            if (refName.EndsWith("/HEAD", StringComparison.Ordinal))
                return null;

            ulong.TryParse(parts[1], out var committerDate);
            var branch = CreateBranch(refName, committerDate, parts[2], parts[3] == "*", parts[4], parts[6]);
            if (branch == null)
                return null;

            if (branch.IsLocal &&
                !string.IsNullOrEmpty(branch.Upstream) &&
                !string.IsNullOrEmpty(parts[5]) &&
                !parts[5].Equals("=", StringComparison.Ordinal))
                mismatched.Add(branch.FullName);

            return branch;
        }

        private static Models.Branch ParseDetailLine(string line)
        {
            var parts = line.Split('\0');
            if (parts.Length != 6)
                return null;

            var refName = parts[0];
            if (refName.EndsWith("/HEAD", StringComparison.Ordinal))
                return null;

            ulong.TryParse(parts[1], out var committerDate);
            return CreateBranch(refName, committerDate, parts[2], parts[3] == "*", parts[4], parts[5]);
        }

        public static Models.Branch CreateBranch(
            string refName,
            ulong committerDate,
            string head,
            bool isCurrent,
            string upstream,
            string worktreePath)
        {
            var branch = new Models.Branch();
            branch.IsDetachedHead = refName.StartsWith(PREFIX_DETACHED_AT, StringComparison.Ordinal) ||
                refName.StartsWith(PREFIX_DETACHED_FROM, StringComparison.Ordinal);

            if (refName.StartsWith(PREFIX_LOCAL, StringComparison.Ordinal))
            {
                branch.Name = refName.Substring(PREFIX_LOCAL.Length);
                branch.IsLocal = true;
            }
            else if (refName.StartsWith(PREFIX_REMOTE, StringComparison.Ordinal))
            {
                var name = refName.Substring(PREFIX_REMOTE.Length);
                var nameParts = name.Split('/', 2);
                if (nameParts.Length != 2)
                    return null;

                branch.Remote = nameParts[0];
                branch.Name = nameParts[1];
                branch.IsLocal = false;
            }
            else
            {
                branch.Name = refName;
                branch.IsLocal = true;
            }

            branch.FullName = refName;
            branch.CommitterDate = committerDate;
            branch.Head = head;
            branch.IsCurrent = isCurrent;
            branch.Upstream = upstream;
            branch.IsUpstreamGone = false;
            branch.WorktreePath = worktreePath;
            return branch;
        }

        public static Models.Branch CreateDetachedHeadBranch(string head)
        {
            var shortHead = head.Length > 10 ? head.Substring(0, 10) : head;
            return new Models.Branch()
            {
                Name = $"(HEAD detached at {shortHead})",
                FullName = $"(HEAD detached at {shortHead})",
                Head = head,
                IsLocal = true,
                IsCurrent = true,
                IsDetachedHead = true,
                IsUpstreamGone = false,
            };
        }

        private static List<string> BuildRefChunks(IReadOnlyList<string> refs, string allRefsTarget)
        {
            if (refs.Count > 128)
                return [allRefsTarget];

            var chunks = new List<string>();
            var builder = new StringBuilder();
            foreach (var r in refs)
            {
                var quoted = r.Quoted();
                if (builder.Length > 0 && builder.Length + quoted.Length + 1 > 24000)
                {
                    chunks.Add(builder.ToString());
                    builder.Clear();
                }

                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(quoted);
            }

            if (builder.Length > 0)
                chunks.Add(builder.ToString());

            return chunks;
        }

        public class DetailsResult
        {
            public bool IsSuccess { get; set; }
            public Dictionary<string, Models.Branch> Branches { get; set; } = new(StringComparer.Ordinal);
        }
    }
}
