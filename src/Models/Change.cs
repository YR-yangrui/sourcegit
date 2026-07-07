namespace SourceGit.Models
{
    public enum ChangeViewMode
    {
        List,
        Grid,
        Tree,
    }

    public enum ChangeState
    {
        None,
        Modified,
        TypeChanged,
        Added,
        Deleted,
        Renamed,
        Copied,
        Untracked,
        Conflicted,
    }

    public enum ConflictReason
    {
        None,
        BothDeleted,
        AddedByUs,
        DeletedByThem,
        AddedByThem,
        DeletedByUs,
        BothAdded,
        BothModified,
    }

    public class ChangeDataForAmend
    {
        public string FileMode { get; set; } = "";
        public string ObjectHash { get; set; } = "";
        public string ParentSHA { get; set; } = "";
    }

    public class Change
    {
        public ChangeState Index { get; set; } = ChangeState.None;
        public ChangeState WorkTree { get; set; } = ChangeState.None;
        public string Path { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public ChangeDataForAmend DataForAmend { get; set; } = null;
        public ConflictReason ConflictReason { get; set; } = ConflictReason.None;
        public bool IsResolvedConflict { get; set; } = false;

        public bool IsConflicted => WorkTree == ChangeState.Conflicted;
        public bool CanResetToConflictState => IsConflicted || IsResolvedConflict;
        public string ConflictMarker => CONFLICT_MARKERS[(int)ConflictReason];
        public string ConflictDesc => CONFLICT_DESCS[(int)ConflictReason];

        public string WorkTreeDesc => TYPE_DESCS[(int)WorkTree];
        public string IndexDesc => TYPE_DESCS[(int)Index];

        public void Set(ChangeState index, ChangeState workTree = ChangeState.None)
        {
            Index = index;
            WorkTree = workTree;

            if (index == ChangeState.Renamed || index == ChangeState.Copied || workTree == ChangeState.Renamed)
            {
                var parts = Path.Split('\t', 2);
                if (parts.Length < 2)
                    parts = Path.Split(" -> ", 2);
                if (parts.Length == 2)
                {
                    OriginalPath = parts[0];
                    Path = parts[1];
                }
            }

            Path = NormalizePath(Path);

            if (!string.IsNullOrEmpty(OriginalPath))
                OriginalPath = NormalizePath(OriginalPath);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '"')
                return path;

            var end = path.Length > 1 && path[^1] == '"' ? path.Length - 1 : path.Length;
            var bytes = new System.Collections.Generic.List<byte>(path.Length);
            for (var i = 1; i < end; i++)
            {
                var ch = path[i];
                if (ch != '\\' || i + 1 >= end)
                {
                    AppendUtf8(bytes, ch);
                    continue;
                }

                var next = path[++i];
                if (next is >= '0' and <= '7')
                {
                    var value = next - '0';
                    for (var j = 0; j < 2 && i + 1 < end && path[i + 1] is >= '0' and <= '7'; j++)
                        value = value * 8 + (path[++i] - '0');

                    bytes.Add((byte)value);
                    continue;
                }

                var unescaped = next switch
                {
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'v' => '\v',
                    _ => next,
                };
                AppendUtf8(bytes, unescaped);
            }

            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static void AppendUtf8(System.Collections.Generic.List<byte> bytes, char ch)
        {
            var encoded = System.Text.Encoding.UTF8.GetBytes([ch]);
            bytes.AddRange(encoded);
        }

        private static readonly string[] TYPE_DESCS =
        [
            "Unknown",
            "Modified",
            "Type Changed",
            "Added",
            "Deleted",
            "Renamed",
            "Copied",
            "Untracked",
            "Conflict"
        ];
        private static readonly string[] CONFLICT_MARKERS =
        [
            string.Empty,
            "DD",
            "AU",
            "UD",
            "UA",
            "DU",
            "AA",
            "UU"
        ];
        private static readonly string[] CONFLICT_DESCS =
        [
            string.Empty,
            "Both deleted",
            "Added by us",
            "Deleted by them",
            "Added by them",
            "Deleted by us",
            "Both added",
            "Both modified"
        ];
    }
}
