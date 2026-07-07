using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

#nullable enable

namespace SourceGit.Models
{
    public static class UpdateChecker
    {
        public static async Task<object> CheckAsync(UpdateChannel channel)
        {
            channel = UpdateChannels.Normalize(channel);

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var releases = await QueryReleaseCandidatesAsync(client, channel).ConfigureAwait(false);
            if (releases.Count == 0)
                return new AlreadyUpToDate();

            var candidates = SelectUpdateCandidates(releases, channel);
            if (candidates.Count == 0)
                return new AlreadyUpToDate();

            var manifests = await DownloadManifestsAsync(client, candidates, channel).ConfigureAwait(false);
            if (manifests.Count == 0)
                return new AlreadyUpToDate();

            var latest = manifests[0];
            var pages = new List<UpdateChangelogPage>(manifests.Count);
            foreach (var item in manifests)
            {
                pages.Add(new UpdateChangelogPage(
                    item.Manifest,
                    channel,
                    item.ReleasePageUrl,
                    item.Candidate.TagName,
                    item.Candidate.ReleasedAtUtc));
            }

            var asset = SelectInstallAsset(latest.Manifest);
            return new UpdateAvailable(latest.Manifest, asset, channel, latest.ReleasePageUrl, pages);
        }

        private static async Task<List<ReleaseCandidate>> QueryReleaseCandidatesAsync(HttpClient client, UpdateChannel channel)
        {
            var limit = GetRetentionLimit(channel);
            var candidates = new List<ReleaseCandidate>(limit);

            for (var page = 1; ; page++)
            {
                var url = $"{GITLAB_BASE_URL}/api/v4/projects/all%2Fsourcegit/releases?order_by=released_at&sort=desc&per_page=100&page={page}";
                var releaseData = await client.GetStringAsync(url).ConfigureAwait(false);
                var releases = JsonSerializer.Deserialize(releaseData, JsonCodeGen.Default.ListGitLabRelease);
                if (releases == null)
                    throw new InvalidOperationException("Cannot parse GitLab releases response.");

                if (releases.Count == 0)
                    break;

                foreach (var release in releases)
                {
                    if (!TryCreateReleaseCandidate(release, channel, out var candidate))
                        continue;

                    candidates.Add(candidate);
                    if (candidates.Count >= limit)
                    {
                        SortCandidates(candidates, channel);
                        return candidates;
                    }
                }
            }

            SortCandidates(candidates, channel);
            return candidates;
        }

        private static List<ReleaseCandidate> SelectUpdateCandidates(List<ReleaseCandidate> releases, UpdateChannel channel)
        {
            if (!TryParseCurrentBuild(channel, out var currentTagName, out var currentVersion, out var currentReleasedAtUtc))
                return new List<ReleaseCandidate>(releases);

            var candidates = new List<ReleaseCandidate>();
            foreach (var release in releases)
            {
                if (release.TagName.Equals(currentTagName, StringComparison.Ordinal))
                    continue;

                if (IsNewerThanCurrent(release, currentVersion, currentReleasedAtUtc, channel))
                    candidates.Add(release);
            }

            return candidates;
        }

        private static async Task<List<ReleaseManifest>> DownloadManifestsAsync(HttpClient client, List<ReleaseCandidate> candidates, UpdateChannel channel)
        {
            var manifests = new List<ReleaseManifest>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var manifestUrl = FindManifestUrl(candidate.Release);
                if (string.IsNullOrWhiteSpace(manifestUrl))
                    throw new InvalidOperationException($"Cannot find sourcegit-update.json in GitLab release assets for {candidate.TagName}.");

                var manifestData = await client.GetStringAsync(NormalizeUrl(manifestUrl)).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize(manifestData, JsonCodeGen.Default.UpdateManifest);
                if (manifest == null)
                    throw new InvalidOperationException($"Cannot parse update manifest for {candidate.TagName}.");

                if (!UpdateChannels.TryParse(manifest.Channel, out var manifestChannel) || manifestChannel != channel)
                    throw new InvalidOperationException($"Update manifest channel for {candidate.TagName} does not match {UpdateChannels.ToManifestChannel(channel)}.");

                if (string.IsNullOrWhiteSpace(manifest.Version))
                    manifest.Version = candidate.TagName;
                else if (!manifest.Version.Equals(candidate.TagName, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Update manifest version for {candidate.TagName} does not match its GitLab release tag.");

                if (manifest.PublishedAt == default)
                    manifest.PublishedAt = candidate.ReleasedAtUtc;

                if (string.IsNullOrWhiteSpace(manifest.PackageVersion))
                    manifest.PackageVersion = manifest.Version;

                manifests.Add(new ReleaseManifest(candidate, manifest));
            }

            return manifests;
        }

        private static string FindManifestUrl(GitLabRelease? release)
        {
            if (release?.Assets?.Links == null)
                return string.Empty;

            foreach (var link in release.Assets.Links)
            {
                if (link.Name.Equals("sourcegit-update.json", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(link.DirectAssetUrl) ? link.Url : link.DirectAssetUrl;
            }

            return string.Empty;
        }

        private static bool TryCreateReleaseCandidate(GitLabRelease release, UpdateChannel channel, out ReleaseCandidate candidate)
        {
            candidate = null!;

            var tagName = release.TagName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tagName))
                return false;

            if (!TryParseTag(tagName, channel, out var parsedVersion))
                return false;

            if (!TryParseGitLabDate(release.ReleasedAt, out var releasedAtUtc))
                throw new InvalidOperationException($"Cannot parse GitLab release date for {tagName}.");

            candidate = new ReleaseCandidate(release, tagName, parsedVersion, releasedAtUtc);
            return true;
        }

        private static bool TryParseCurrentBuild(UpdateChannel channel, out string tagName, out ParsedReleaseVersion version, out DateTime releasedAtUtc)
        {
            tagName = BuildInfo.Version?.Trim() ?? string.Empty;
            version = default;
            releasedAtUtc = BuildInfo.BuildDateUtc.ToUniversalTime();

            if (!UpdateChannels.TryParse(BuildInfo.Channel, out var buildChannel) || buildChannel != channel)
                return false;

            return TryParseTag(tagName, channel, out version);
        }

        private static bool TryParseTag(string tagName, UpdateChannel channel, out ParsedReleaseVersion version)
        {
            version = default;

            if (channel == UpdateChannel.Stable)
            {
                var match = STABLE_TAG_REGEX.Match(tagName);
                if (!match.Success ||
                    !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
                    !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
                    !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
                    return false;

                version = ParsedReleaseVersion.ForStable(major, minor, patch);
                return true;
            }

            var nightlyMatch = NIGHTLY_TAG_REGEX.Match(tagName);
            if (!nightlyMatch.Success ||
                !int.TryParse(nightlyMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
                !int.TryParse(nightlyMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
                !int.TryParse(nightlyMatch.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
                return false;

            var dateText = $"{year:D4}.{month:D2}.{day:D2}";
            if (!DateTime.TryParseExact(dateText, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return false;

            version = ParsedReleaseVersion.ForNightly(year * 10000 + month * 100 + day);
            return true;
        }

        private static bool TryParseGitLabDate(string value, out DateTime utc)
        {
            utc = default;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return false;

            utc = parsed.UtcDateTime;
            return true;
        }

        private static bool IsNewerThanCurrent(ReleaseCandidate release, ParsedReleaseVersion currentVersion, DateTime currentReleasedAtUtc, UpdateChannel channel)
        {
            var versionCompare = CompareVersion(release.Version, currentVersion, channel);
            if (versionCompare != 0)
                return versionCompare > 0;

            return release.ReleasedAtUtc > currentReleasedAtUtc;
        }

        private static void SortCandidates(List<ReleaseCandidate> candidates, UpdateChannel channel)
        {
            candidates.Sort((l, r) =>
            {
                var versionCompare = CompareVersion(l.Version, r.Version, channel);
                if (versionCompare != 0)
                    return -versionCompare;

                return -l.ReleasedAtUtc.CompareTo(r.ReleasedAtUtc);
            });
        }

        private static int CompareVersion(ParsedReleaseVersion left, ParsedReleaseVersion right, UpdateChannel channel)
        {
            if (channel == UpdateChannel.Stable)
            {
                var majorCompare = left.Major.CompareTo(right.Major);
                if (majorCompare != 0)
                    return majorCompare;

                var minorCompare = left.Minor.CompareTo(right.Minor);
                if (minorCompare != 0)
                    return minorCompare;

                return left.Patch.CompareTo(right.Patch);
            }

            return left.NightlyDate.CompareTo(right.NightlyDate);
        }

        private static int GetRetentionLimit(UpdateChannel channel)
        {
            return channel == UpdateChannel.Nightly ? 90 : 30;
        }

        private static UpdateAsset? SelectInstallAsset(UpdateManifest manifest)
        {
            if (!OperatingSystem.IsWindows())
                return null;

            var runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            return manifest.FindAsset(runtime, "self-update-zip");
        }

        private static string NormalizeUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
                return url;

            if (url.StartsWith("/", StringComparison.Ordinal))
                return $"{GITLAB_BASE_URL}{url}";

            return $"{GITLAB_BASE_URL}/{url}";
        }

        private static string GetReleasePageUrl(string tagName)
        {
            return $"{GITLAB_BASE_URL}/all/sourcegit/-/releases/{Uri.EscapeDataString(tagName)}";
        }

        private readonly struct ParsedReleaseVersion
        {
            public int Major { get; }

            public int Minor { get; }

            public int Patch { get; }

            public int NightlyDate { get; }

            private ParsedReleaseVersion(int major, int minor, int patch, int nightlyDate)
            {
                Major = major;
                Minor = minor;
                Patch = patch;
                NightlyDate = nightlyDate;
            }

            public static ParsedReleaseVersion ForStable(int major, int minor, int patch)
            {
                return new ParsedReleaseVersion(major, minor, patch, 0);
            }

            public static ParsedReleaseVersion ForNightly(int nightlyDate)
            {
                return new ParsedReleaseVersion(0, 0, 0, nightlyDate);
            }
        }

        private sealed class ReleaseCandidate
        {
            public GitLabRelease Release { get; }

            public string TagName { get; }

            public ParsedReleaseVersion Version { get; }

            public DateTime ReleasedAtUtc { get; }

            public ReleaseCandidate(GitLabRelease release, string tagName, ParsedReleaseVersion version, DateTime releasedAtUtc)
            {
                Release = release;
                TagName = tagName;
                Version = version;
                ReleasedAtUtc = releasedAtUtc;
            }
        }

        private sealed class ReleaseManifest
        {
            public ReleaseCandidate Candidate { get; }

            public UpdateManifest Manifest { get; }

            public string ReleasePageUrl => GetReleasePageUrl(Candidate.TagName);

            public ReleaseManifest(ReleaseCandidate candidate, UpdateManifest manifest)
            {
                Candidate = candidate;
                Manifest = manifest;
            }
        }

        private static readonly Regex STABLE_TAG_REGEX = new(@"^stable-(\d+)\.(\d+)\.(\d+)\.[0-9a-f]+$", RegexOptions.CultureInvariant);
        private static readonly Regex NIGHTLY_TAG_REGEX = new(@"^nightly-(\d{4})\.(\d{2})\.(\d{2})\.[0-9a-f]+$", RegexOptions.CultureInvariant);

        private const string GITLAB_BASE_URL = "http://gitlab.zjhuayu.top";
    }
}
