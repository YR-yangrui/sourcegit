using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class DeleteTag : Popup
    {
        public Models.Tag Target
        {
            get;
            private set;
        }

        public bool PushToRemotes
        {
            get => _repo.UIStates.PushToRemoteWhenDeleteTag;
            set => _repo.UIStates.PushToRemoteWhenDeleteTag = value;
        }

        public DeleteTag(Repository repo, Models.Tag tag)
        {
            _repo = repo;
            Target = tag;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Deleting tag '{Target.Name}' ...";

            var log = _repo.CreateLog("Delete Tag");
            Use(log);

            var succ = await new Commands.Tag(_repo.FullPath, Target.Name)
                .Use(log)
                .DeleteAsync();

            if (succ && PushToRemotes)
            {
                var cmd = new Commands.Remote(_repo.FullPath).Use(log);
                foreach (var remote in _repo.Remotes)
                {
                    var result = await cmd.GetExistingTagsAsync(remote.Name, [Target.Name]);
                    if (!result.IsSuccess)
                    {
                        succ = false;
                        break;
                    }

                    if (!result.Tags.Contains(Target.Name))
                        continue;

                    await new Commands.Push(_repo.FullPath, remote.Name, $"refs/tags/{Target.Name}", true)
                        .Use(log)
                        .RunAsync();
                }
            }

            log.Complete();
            _repo.UIStates.RemoveHistoryFilter(Target.Name, Models.FilterType.Tag);
            _repo.MarkTagsDirtyManually();
            return succ;
        }

        private readonly Repository _repo = null;
    }
}
