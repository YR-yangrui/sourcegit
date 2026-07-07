using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class DropStash : Popup
    {
        public List<Models.Stash> Stashes { get; }

        public string TargetDescription { get; }

        public DropStash(Repository repo, Models.Stash stash)
            : this(repo, [stash])
        {
        }

        public DropStash(Repository repo, List<Models.Stash> stashes)
        {
            _repo = repo;
            Stashes = SortForDrop(stashes);
            TargetDescription = string.Join(", ", Stashes.ConvertAll(x => x.Name));
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = Stashes.Count == 1 ?
                $"Dropping stash: {Stashes[0].Name}" :
                $"Dropping {Stashes.Count} stashes";

            var log = _repo.CreateLog(Stashes.Count == 1 ? "Drop Stash" : "Drop Stashes");
            Use(log);

            foreach (var stash in Stashes)
            {
                await new Commands.Stash(_repo.FullPath)
                    .Use(log)
                    .DropAsync(stash.Name);
            }

            log.Complete();
            _repo.MarkStashesDirtyManually();
            return true;
        }

        private static List<Models.Stash> SortForDrop(List<Models.Stash> stashes)
        {
            var sorted = new List<Models.Stash>(stashes);
            // Dropping stash@{n} renumbers later entries, so delete larger indexes first to preserve the selected targets.
            sorted.Sort((l, r) => GetStashIndex(r.Name).CompareTo(GetStashIndex(l.Name)));
            return sorted;
        }

        private static int GetStashIndex(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith("stash@{", StringComparison.Ordinal) || !name.EndsWith('}'))
                return -1;

            var value = name.Substring(7, name.Length - 8);
            return int.TryParse(value, out var index) ? index : -1;
        }

        private readonly Repository _repo;
    }
}
