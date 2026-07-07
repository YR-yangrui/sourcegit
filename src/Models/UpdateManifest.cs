using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

#nullable enable

namespace SourceGit.Models
{
    public class UpdateManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("baseVersion")]
        public string BaseVersion { get; set; } = string.Empty;

        [JsonPropertyName("packageVersion")]
        public string PackageVersion { get; set; } = string.Empty;

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        [JsonPropertyName("commit")]
        public string Commit { get; set; } = string.Empty;

        [JsonPropertyName("pipelineId")]
        public string PipelineId { get; set; } = string.Empty;

        [JsonPropertyName("pipelineIid")]
        public string PipelineIid { get; set; } = string.Empty;

        [JsonPropertyName("publishedAt")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("releaseNotes")]
        public string ReleaseNotes { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<UpdateAsset> Assets { get; set; } = [];

        public UpdateAsset? FindAsset(string runtime, string kind)
        {
            if (string.IsNullOrWhiteSpace(runtime) || string.IsNullOrWhiteSpace(kind))
                return null;

            foreach (var asset in Assets)
            {
                if (asset.Runtime.Equals(runtime, StringComparison.OrdinalIgnoreCase) &&
                    asset.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                    return asset;
            }

            return null;
        }
    }

    public class UpdateAsset
    {
        [JsonPropertyName("runtime")]
        public string Runtime { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
