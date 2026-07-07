using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Remote : Command
    {
        public Remote(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> AddAsync(string name, string url)
        {
            Args = $"remote add {name} {url}";
            return await ExecAsync();
        }

        public async Task<bool> DeleteAsync(string name)
        {
            Args = $"remote remove {name}";
            return await ExecAsync();
        }

        public async Task<bool> RenameAsync(string name, string to)
        {
            Args = $"remote rename {name} {to}";
            return await ExecAsync();
        }

        public async Task<bool> PruneAsync(string name)
        {
            Args = $"remote prune {name}";
            return await ExecAsync();
        }

        public async Task<string> GetURLAsync(string name, bool isPush)
        {
            Args = "remote get-url" + (isPush ? " --push " : " ") + name;

            var rs = await ReadToEndAsync();
            return rs.IsSuccess ? rs.StdOut.Trim() : string.Empty;
        }

        public async Task<bool> SetURLAsync(string name, string url, bool isPush)
        {
            Args = "remote set-url" + (isPush ? " --push " : " ") + $"{name} {url}";
            return await ExecAsync();
        }

        public async Task<bool> HasBranchAsync(string remote, string branch)
        {
            SSHKey = await new Config(WorkingDirectory).GetAsync($"remote.{remote}.sshkey");
            Args = $"ls-remote {remote} {branch}";

            var rs = await ReadToEndAsync();
            return rs.IsSuccess && rs.StdOut.Trim().Length > 0;
        }

        public async Task<bool> HasTagAsync(string remote, string tag)
        {
            var result = await GetExistingTagsAsync(remote, [tag]);
            return result.IsSuccess && result.Tags.Contains(tag);
        }

        public async Task<TagQueryResult> GetExistingTagsAsync(string remote, IReadOnlyList<string> tags)
        {
            var result = new TagQueryResult();
            if (tags == null || tags.Count == 0)
            {
                result.IsSuccess = true;
                return result;
            }

            SSHKey = await new Config(WorkingDirectory).GetAsync($"remote.{remote}.sshkey");
            foreach (var chunk in BuildTagRefChunks(tags))
            {
                Args = $"ls-remote --tags --refs {remote} {chunk}";
                Log?.AppendLine($"$ git {Args}\n");

                var rs = await ReadToEndAsync();
                if (!rs.IsSuccess)
                {
                    var err = rs.StdErr.Trim();
                    if (string.IsNullOrEmpty(err))
                        err = $"Failed to query tags from remote '{remote}'.";

                    Log?.AppendLine(err);
                    Log?.AppendLine(string.Empty);
                    if (RaiseError)
                        RaiseException(err);

                    return result;
                }

                var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var tabIdx = line.LastIndexOf('\t');
                    if (tabIdx < 0 || tabIdx + 1 >= line.Length)
                        continue;

                    var refName = line.Substring(tabIdx + 1);
                    if (refName.StartsWith("refs/tags/", StringComparison.Ordinal))
                        result.Tags.Add(refName.Substring("refs/tags/".Length));
                }
            }

            result.IsSuccess = true;
            return result;
        }

        private static List<string> BuildTagRefChunks(IReadOnlyList<string> tags)
        {
            var chunks = new List<string>();
            var builder = new StringBuilder();
            foreach (var tag in tags)
            {
                var quoted = $"refs/tags/{tag}".Quoted();
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

        public class TagQueryResult
        {
            public bool IsSuccess { get; set; } = false;
            public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);
        }
    }
}
