using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryRefSnapshot : Command
    {
        private const string PREFIX_LOCAL = "refs/heads/";
        private const string PREFIX_REMOTE = "refs/remotes/";
        private const string PREFIX_TAG = "refs/tags/";

        public QueryRefSnapshot(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = "for-each-ref --format=\"%(refname)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(worktreepath)\" refs/heads refs/remotes refs/tags";
        }

        public async Task<Snapshot> GetResultAsync()
        {
            var snapshot = new Snapshot();
            using var span = StartGitDiagnosticSpan("query_ref_snapshot");
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
            {
                span.Set("success", false);
                return snapshot;
            }

            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            span.Set("lineCount", lines.Length);

            foreach (var line in lines)
            {
                var parts = line.Split('\0');
                if (parts.Length != 5)
                    continue;

                var row = new RefRow()
                {
                    FullName = parts[0],
                    ObjectName = parts[1],
                    IsCurrent = parts[2] == "*",
                    Upstream = parts[3],
                    WorktreePath = parts[4],
                };

                if (string.IsNullOrEmpty(row.FullName) || string.IsNullOrEmpty(row.ObjectName))
                    continue;

                snapshot.Rows.Add(row);
                snapshot.ObjectNames[row.FullName] = row.ObjectName;

                if (row.IsCurrent && string.IsNullOrEmpty(snapshot.HeadSHA))
                    snapshot.HeadSHA = row.ObjectName;
            }

            snapshot.IsSuccess = true;
            snapshot.RefsFingerprint = BuildRefsFingerprint(snapshot.ObjectNames);

            span.Set("success", true);
            span.Set("refCount", snapshot.Rows.Count);
            span.Set("branchRefCount", snapshot.BranchRefCount);
            span.Set("tagRefCount", snapshot.TagRefCount);
            span.Set("hasHead", !string.IsNullOrEmpty(snapshot.HeadSHA));
            return snapshot;
        }

        private static string BuildRefsFingerprint(Dictionary<string, string> refs)
        {
            if (refs.Count == 0)
                return string.Empty;

            var entries = new List<string>(refs.Count);
            foreach (var kv in refs)
                entries.Add($"{kv.Key}\0{kv.Value}");

            entries.Sort(StringComparer.Ordinal);

            var builder = new StringBuilder(entries.Count * 80);
            foreach (var entry in entries)
                builder.Append(entry).Append('\n');

            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        public class Snapshot
        {
            public bool IsSuccess { get; set; }
            public List<RefRow> Rows { get; } = [];
            public Dictionary<string, string> ObjectNames { get; } = new(StringComparer.Ordinal);
            public string HeadSHA { get; set; } = string.Empty;
            public string RefsFingerprint { get; set; } = string.Empty;

            public int BranchRefCount
            {
                get
                {
                    var count = 0;
                    foreach (var row in Rows)
                    {
                        if (row.IsBranch && !row.IsRemoteHEAD)
                            count++;
                    }

                    return count;
                }
            }

            public int TagRefCount
            {
                get
                {
                    var count = 0;
                    foreach (var row in Rows)
                    {
                        if (row.IsTag)
                            count++;
                    }

                    return count;
                }
            }
        }

        public class RefRow
        {
            public string FullName { get; set; } = string.Empty;
            public string ObjectName { get; set; } = string.Empty;
            public bool IsCurrent { get; set; }
            public string Upstream { get; set; } = string.Empty;
            public string WorktreePath { get; set; } = string.Empty;

            public bool IsLocalBranch => FullName.StartsWith(PREFIX_LOCAL, StringComparison.Ordinal);
            public bool IsRemoteBranch => FullName.StartsWith(PREFIX_REMOTE, StringComparison.Ordinal);
            public bool IsBranch => IsLocalBranch || IsRemoteBranch;
            public bool IsRemoteHEAD => IsRemoteBranch && FullName.EndsWith("/HEAD", StringComparison.Ordinal);
            public bool IsTag => FullName.StartsWith(PREFIX_TAG, StringComparison.Ordinal);
        }
    }
}
