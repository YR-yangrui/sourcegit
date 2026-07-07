using System;
using System.Collections.Generic;

#nullable enable

namespace SourceGit.Models
{
    public class Version
    {
        public virtual string TagName { get; protected set; } = string.Empty;

        public virtual string Body { get; protected set; } = string.Empty;

        public virtual string CurrentVersionStr => BuildInfo.DisplayVersion;

        public virtual string ReleaseDateStr { get; protected set; } = string.Empty;
    }

    public class UpdateAvailable : Version
    {
        public UpdateManifest Manifest { get; }

        public UpdateAsset? Asset { get; }

        public UpdateChannel Channel { get; }

        public string ReleasePageUrl { get; }

        public IReadOnlyList<UpdateChangelogPage> ChangelogPages { get; }

        public string ChannelName => UpdateChannels.GetDisplayText(Channel);

        public bool CanInstall => OperatingSystem.IsWindows() && Asset != null;

        public UpdateAvailable(UpdateManifest manifest, UpdateAsset? asset, UpdateChannel channel, string releasePageUrl)
            : this(manifest, asset, channel, releasePageUrl, new[] { new UpdateChangelogPage(manifest, channel, releasePageUrl) })
        {
        }

        public UpdateAvailable(UpdateManifest manifest, UpdateAsset? asset, UpdateChannel channel, string releasePageUrl, IReadOnlyList<UpdateChangelogPage> changelogPages)
        {
            Manifest = manifest;
            Asset = asset;
            Channel = UpdateChannels.Normalize(channel);
            ReleasePageUrl = releasePageUrl;
            ChangelogPages = changelogPages.Count > 0 ? changelogPages : new[] { new UpdateChangelogPage(manifest, Channel, releasePageUrl) };

            var latest = ChangelogPages[0];
            TagName = latest.TagName;
            Body = latest.Body;
            ReleaseDateStr = latest.ReleaseDateStr;
        }
    }

    public class UpdateChangelogPage : Version
    {
        public UpdateManifest Manifest { get; }

        public UpdateChannel Channel { get; }

        public string ReleasePageUrl { get; }

        public string ChannelName => UpdateChannels.GetDisplayText(Channel);

        public UpdateChangelogPage(UpdateManifest manifest, UpdateChannel channel, string releasePageUrl, string fallbackVersion = "", DateTime? fallbackPublishedAt = null)
        {
            Manifest = manifest;
            Channel = UpdateChannels.Normalize(channel);
            ReleasePageUrl = releasePageUrl;

            TagName = string.IsNullOrWhiteSpace(manifest.Version) ? fallbackVersion : manifest.Version;
            Body = manifest.ReleaseNotes ?? string.Empty;

            var publishedAt = manifest.PublishedAt == default && fallbackPublishedAt.HasValue ? fallbackPublishedAt.Value : manifest.PublishedAt;
            ReleaseDateStr = publishedAt == default ? string.Empty : DateTimeFormat.Format(publishedAt.ToLocalTime(), true);
        }
    }

    public class AlreadyUpToDate;

    public class SelfUpdateFailed
    {
        public string Reason
        {
            get;
            private set;
        }

        public SelfUpdateFailed(Exception e)
        {
            if (e.InnerException is { } inner)
                Reason = inner.Message;
            else
                Reason = e.Message;
        }
    }
}
