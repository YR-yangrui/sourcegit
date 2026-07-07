using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class RepositoryInitializationConfig
    {
        public static async Task<bool> EnsureAsync(string repository, Models.ICommandLog log)
        {
            var config = new Config(repository).Use(log);
            foreach (var (key, value, minGitVersion) in TargetValues)
            {
                if (minGitVersion != null && Native.OS.GitVersion < minGitVersion)
                    continue;

                var current = await config.GetLocalValuesAsync(key).ConfigureAwait(false);
                if (current.Count == 1 && string.Equals(current[0], value, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!await config.SetLocalReplaceAllAsync(key, value).ConfigureAwait(false))
                    return false;
            }

            return true;
        }

        private static readonly (string Key, string Value, Version MinGitVersion)[] TargetValues =
        [
            ("core.fsmonitor", "true", Models.GitVersions.BUILTIN_FSMONITOR),
            ("core.untrackedCache", "true", null),
            ("feature.manyFiles", "true", null),
            ("status.showUntrackedFiles", "all", null),
        ];
    }
}
