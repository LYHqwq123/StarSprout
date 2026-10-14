using Newtonsoft.Json;

namespace StarSprout.Services;

public sealed class CommunityState
{
    [JsonProperty("sources")] public List<CommunitySource> Sources { get; set; } = new();
    [JsonProperty("offline")] public bool Offline { get; set; }
}

public sealed class CommunitySource
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("sourceUrl")] public string SourceUrl { get; set; } = "";
    [JsonProperty("supportsApi")] public bool SupportsApi { get; set; }
    [JsonProperty("description")] public string Description { get; set; } = "";
}

public sealed class CommunityCatalog
{
    [JsonProperty("items")] public List<CommunityMod> Items { get; set; } = new();
    [JsonProperty("page")] public int Page { get; set; } = 1;
    [JsonProperty("hasMore")] public bool HasMore { get; set; }
    [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new();
}

public sealed class CommunityMod
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("source")] public string Source { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("summary")] public string Summary { get; set; } = "";
    [JsonProperty("author")] public string Author { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("sourceUrl")] public string SourceUrl { get; set; } = "";
    [JsonProperty("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonProperty("thumbnailUrl")] public string ThumbnailUrl { get; set; } = "";
    [JsonProperty("description")] public string Description { get; set; } = "";
    [JsonProperty("downloads")] public List<CommunityDownload> Downloads { get; set; } = new();
    [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new();
}

public sealed class CommunityDownload
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("url")] public string Url { get; set; } = "";
    [JsonProperty("canInstall")] public bool CanInstall { get; set; }
}
