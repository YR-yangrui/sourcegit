using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class DeleteMultipleTags : Popup
    {
        public List<Models.Tag> Tags
        {
            get;
        }

        public bool DeleteFromRemote
        {
            get;
            set;
        } = false;

        public DeleteMultipleTags(Repository repo, List<Models.Tag> tags)
        {
            _repo = repo;
            Tags = tags;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "Deleting multiple tags...";

            var log = _repo.CreateLog("Delete Multiple Tags");
            Use(log);

            var allSucceeded = true;
            var deleted = new List<Models.Tag>();
            foreach (var tag in Tags)
            {
                var succ = await new Commands.Tag(_repo.FullPath, tag.Name)
                    .Use(log)
                    .DeleteAsync();

                if (succ)
                {
                    if (DeleteFromRemote)
                        deleted.Add(tag);
                }
                else
                {
                    allSucceeded = false;
                }
            }

            if (deleted.Count > 0)
            {
                var tagNames = new List<string>(deleted.Count);
                foreach (var tag in deleted)
                    tagNames.Add(tag.Name);

                var cmd = new Commands.Remote(_repo.FullPath).Use(log);
                foreach (var remote in _repo.Remotes)
                {
                    var result = await cmd.GetExistingTagsAsync(remote.Name, tagNames);
                    if (!result.IsSuccess)
                    {
                        allSucceeded = false;
                        break;
                    }

                    var existingTags = result.Tags;
                    if (existingTags.Count == 0)
                        continue;

                    var deletingRefs = new List<string>();
                    foreach (var tag in deleted)
                    {
                        if (existingTags.Contains(tag.Name))
                            deletingRefs.Add($"refs/tags/{tag.Name}");
                    }

                    foreach (var refChunk in BuildRefChunks(deletingRefs))
                    {
                        await new Commands.Push(_repo.FullPath, remote.Name, refChunk, true)
                            .Use(log)
                            .RunAsync();
                    }
                }
            }

            log.Complete();
            _repo.MarkTagsDirtyManually();
            return allSucceeded;
        }

        private static List<List<string>> BuildRefChunks(List<string> refs)
        {
            var chunks = new List<List<string>>();
            var chunk = new List<string>();
            var chunkLength = 0;

            foreach (var refname in refs)
            {
                if (chunk.Count > 0 && chunkLength + refname.Length + 1 > 24000)
                {
                    chunks.Add(chunk);
                    chunk = new List<string>();
                    chunkLength = 0;
                }

                chunk.Add(refname);
                chunkLength += refname.Length + 1;
            }

            if (chunk.Count > 0)
                chunks.Add(chunk);

            return chunks;
        }

        private readonly Repository _repo;
    }
}
