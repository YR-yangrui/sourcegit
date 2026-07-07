using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class CountLocalChanges : Command
    {
        public CountLocalChanges(string repo, bool includeUntracked)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"--no-optional-locks {GetStatusUntrackedArg(includeUntracked)} --ignore-submodules=all --porcelain";
        }

        public async Task<int> GetResultAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (rs.IsSuccess)
            {
                var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                return lines.Length;
            }

            return 0;
        }
    }
}
