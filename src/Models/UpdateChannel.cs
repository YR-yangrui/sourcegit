using System;

namespace SourceGit.Models
{
    public enum UpdateChannel
    {
        Stable = 0,
        Nightly = 1,
    }

    public static class UpdateChannels
    {
        public static UpdateChannel Normalize(UpdateChannel channel)
        {
            return channel is UpdateChannel.Stable or UpdateChannel.Nightly ? channel : UpdateChannel.Stable;
        }

        public static string GetDisplayText(UpdateChannel channel)
        {
            return Normalize(channel) == UpdateChannel.Nightly ? App.Text("UpdateChannel.Nightly") : App.Text("UpdateChannel.Stable");
        }

        public static UpdateChannel Parse(string value)
        {
            return TryParse(value, out var channel) ? channel : UpdateChannel.Stable;
        }

        public static bool TryParse(string value, out UpdateChannel channel)
        {
            channel = UpdateChannel.Stable;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim();
            if (normalized.Equals("stable", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Equals("nightly", StringComparison.OrdinalIgnoreCase))
            {
                channel = UpdateChannel.Nightly;
                return true;
            }

            return false;
        }

        public static string ToManifestChannel(UpdateChannel channel)
        {
            return Normalize(channel) == UpdateChannel.Nightly ? "nightly" : "stable";
        }
    }
}
