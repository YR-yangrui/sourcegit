using System;
using System.Linq;
using System.Reflection;

namespace SourceGit.Models
{
    public static class BuildInfo
    {
        public static string Channel { get; } = GetMetadata("SourceGitChannel", "local");

        public static string Version { get; } = GetMetadata("SourceGitVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty);

        public static string BaseVersion { get; } = GetMetadata("SourceGitBaseVersion", Version);

        public static string PipelineIid { get; } = GetMetadata("SourceGitPipelineIid", string.Empty);

        public static string Commit { get; } = GetMetadata("SourceGitCommit", string.Empty);

        public static DateTime BuildDateUtc { get; } = GetBuildDateUtc();

        public static string DisplayVersion
        {
            get
            {
                var version = string.IsNullOrWhiteSpace(Version) ? BaseVersion : Version;
                if (string.IsNullOrWhiteSpace(Channel) || Channel.Equals("local", StringComparison.OrdinalIgnoreCase))
                    return version;

                if (!string.IsNullOrWhiteSpace(PipelineIid))
                    return $"{version} ({Channel}.{PipelineIid})";

                return $"{version} ({Channel})";
            }
        }

        private static string GetMetadata(string key, string fallback)
        {
            var value = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(x => x.Key.Equals(key, StringComparison.Ordinal))?
                .Value;

            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static DateTime GetBuildDateUtc()
        {
            var raw = GetMetadata("SourceGitBuildDate", string.Empty);
            if (DateTimeOffset.TryParse(raw, out var parsed))
                return parsed.UtcDateTime;

            return DateTime.UtcNow;
        }
    }
}
