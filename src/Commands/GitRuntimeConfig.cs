using System;
using System.Collections.Concurrent;
using System.Text;

namespace SourceGit.Commands
{
    public static class GitRuntimeConfig
    {
        public static void Register(string repo, string gitDir, string gitCommonDir, Models.RepositorySettings settings)
        {
            Register(repo, settings);
            Register(gitDir, settings);
            Register(gitCommonDir, settings);
        }

        public static void Unregister(string repo, string gitDir, string gitCommonDir)
        {
            Unregister(repo);
            Unregister(gitDir);
            Unregister(gitCommonDir);
        }

        public static void Append(StringBuilder builder, string repo)
        {
            var settings = ResolveSettings(repo);
            if (settings?.EnableLFSLockableFiles is { } enabled)
                builder.Append("-c lfs.setlockablereadonly=").Append(enabled ? "true" : "false").Append(' ');
        }

        private static void Register(string path, Models.RepositorySettings settings)
        {
            if (string.IsNullOrWhiteSpace(path) || settings == null)
                return;

            _settingsByPath[Normalize(path)] = settings;
        }

        private static void Unregister(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                _settingsByPath.TryRemove(Normalize(path), out _);
        }

        private static Models.RepositorySettings ResolveSettings(string repo)
        {
            if (string.IsNullOrWhiteSpace(repo))
                return null;

            return _settingsByPath.TryGetValue(Normalize(repo), out var settings) ? settings : null;
        }

        private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

        private static readonly ConcurrentDictionary<string, Models.RepositorySettings> _settingsByPath = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }
}
