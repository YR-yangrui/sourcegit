using System;

namespace SourceGit.Models
{
    public enum HistoryQueryScopeKind
    {
        AllBranches,
        CurrentBranch,
        Head,
        Revision,
        Branch,
    }

    public class HistoryQueryScope
    {
        public HistoryQueryScopeKind Kind { get; init; }
        public Branch Branch { get; init; }
        public string Revision { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        public bool IsAllBranches => Kind == HistoryQueryScopeKind.AllBranches;
        public bool IsCurrentHead => Kind is HistoryQueryScopeKind.CurrentBranch or HistoryQueryScopeKind.Head;

        public string RevisionArg => Kind switch
        {
            HistoryQueryScopeKind.Revision => Revision,
            HistoryQueryScopeKind.Branch => Branch?.FullName ?? string.Empty,
            _ => string.Empty,
        };

        public string ShortRevision
        {
            get
            {
                var rev = Kind == HistoryQueryScopeKind.Branch ? Branch?.Head : Revision;
                if (string.IsNullOrEmpty(rev))
                    return string.Empty;

                return rev.Length > 10 ? rev[..10] : rev;
            }
        }

        public bool Matches(string filter)
        {
            return string.IsNullOrEmpty(filter) ||
                Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                Revision.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (Branch?.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        public static HistoryQueryScope AllBranches(string name)
        {
            return new HistoryQueryScope()
            {
                Kind = HistoryQueryScopeKind.AllBranches,
                Name = name,
            };
        }

        public static HistoryQueryScope CurrentBranch(string name, Branch branch)
        {
            return new HistoryQueryScope()
            {
                Kind = HistoryQueryScopeKind.CurrentBranch,
                Branch = branch,
                Name = name,
            };
        }

        public static HistoryQueryScope Head(string name)
        {
            return new HistoryQueryScope()
            {
                Kind = HistoryQueryScopeKind.Head,
                Name = name,
            };
        }

        public static HistoryQueryScope RevisionScope(string revision)
        {
            var name = string.IsNullOrEmpty(revision) ? string.Empty : (revision.Length > 10 ? revision[..10] : revision);
            return new HistoryQueryScope()
            {
                Kind = HistoryQueryScopeKind.Revision,
                Revision = revision,
                Name = name,
            };
        }

        public static HistoryQueryScope BranchScope(Branch branch)
        {
            return new HistoryQueryScope()
            {
                Kind = HistoryQueryScopeKind.Branch,
                Branch = branch,
                Name = branch.FriendlyName,
            };
        }
    }
}
