using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SourceGit.Models
{
    public class GitLabRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("released_at")]
        public string ReleasedAt { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public GitLabReleaseAssets Assets { get; set; } = new();
    }

    public class GitLabReleaseAssets
    {
        [JsonPropertyName("links")]
        public List<GitLabReleaseLink> Links { get; set; } = [];
    }

    public class GitLabReleaseLink
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("direct_asset_url")]
        public string DirectAssetUrl { get; set; } = string.Empty;
    }
}
