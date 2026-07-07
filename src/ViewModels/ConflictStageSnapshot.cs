using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class ConflictStageSnapshot
    {
        public IReadOnlyList<Commands.MergeConflictBlob.StageEntry> Entries
        {
            get;
            private init;
        } = [];

        public IReadOnlyList<string> Paths
        {
            get;
            private init;
        } = [];

        public string Hash
        {
            get;
            private init;
        } = string.Empty;

        public bool IsEmpty => Entries.Count == 0;

        public static async Task<ConflictStageSnapshot> QueryAsync(string repo, CancellationToken cancellation)
        {
            var entries = await Commands.MergeConflictBlob.QueryConflictEntriesAsync(repo, cancellation)
                .ConfigureAwait(false);

            entries.Sort(CompareEntries);

            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var builder = new StringBuilder();

            foreach (var entry in entries)
            {
                builder.Append(entry.Mode)
                    .Append(' ')
                    .Append(entry.ObjectId)
                    .Append(' ')
                    .Append(entry.Stage)
                    .Append('\t')
                    .Append(entry.Path)
                    .Append('\n');

                if (seen.Add(entry.Path))
                    paths.Add(entry.Path);
            }

            return new ConflictStageSnapshot
            {
                Entries = entries,
                Paths = paths,
                Hash = CalculateHash(builder.ToString()),
            };
        }

        private static int CompareEntries(Commands.MergeConflictBlob.StageEntry left, Commands.MergeConflictBlob.StageEntry right)
        {
            var pathCompare = string.Compare(left.Path, right.Path, StringComparison.Ordinal);
            if (pathCompare != 0)
                return pathCompare;

            var stageCompare = left.Stage.CompareTo(right.Stage);
            if (stageCompare != 0)
                return stageCompare;

            var oidCompare = string.Compare(left.ObjectId, right.ObjectId, StringComparison.Ordinal);
            if (oidCompare != 0)
                return oidCompare;

            return string.Compare(left.Mode, right.Mode, StringComparison.Ordinal);
        }

        private static string CalculateHash(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
