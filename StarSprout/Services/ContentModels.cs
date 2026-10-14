using Newtonsoft.Json;

namespace StarSprout.Services;

public sealed class ContentCatalog
{
    [JsonProperty("items")] public List<ContentItem> Items { get; set; } = new();
    [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new();
    [JsonProperty("offline")] public bool Offline { get; set; }
    [JsonProperty("lastUpdated")] public string LastUpdated { get; set; } = "";
}

public sealed class ContentItem
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("author")] public string Author { get; set; } = "";
    [JsonProperty("description")] public string Description { get; set; } = "";
    [JsonProperty("details")] public string Details { get; set; } = "";
    [JsonProperty("kind")] public string Kind { get; set; } = "mod";
    [JsonProperty("sourceUrl")] public string SourceUrl { get; set; } = "";
    [JsonProperty("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonProperty("githubRepository")] public string GithubRepository { get; set; } = "";
    [JsonProperty("assetPrefix")] public string AssetPrefix { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    [JsonProperty("dependencies")] public List<string> Dependencies { get; set; } = new();
    [JsonProperty("source")] public string Source { get; set; } = "";
    [JsonProperty("cached")] public bool Cached { get; set; }
    [JsonProperty("canDownload")] public bool CanDownload => DownloadUrl.Length > 0 || GithubRepository.Length > 0;
}

public sealed class ContentState
{
    [JsonProperty("offline")] public bool Offline { get; set; }
    [JsonProperty("feeds")] public List<string> Feeds { get; set; } = new();
    [JsonProperty("downloads")] public List<DownloadedContent> Downloads { get; set; } = new();
    [JsonProperty("cacheDirectory")] public string CacheDirectory { get; set; } = "";
}

public sealed class DownloadedContent
{
    [JsonProperty("itemId")] public string ItemId { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("fileName")] public string FileName { get; set; } = "";
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    [JsonProperty("size")] public long Size { get; set; }
    [JsonProperty("downloadedAt")] public string DownloadedAt { get; set; } = "";
    [JsonProperty("sourceUrl")] public string SourceUrl { get; set; } = "";
}

public sealed class DownloadResult
{
    [JsonProperty("itemId")] public string ItemId { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("path")] public string Path { get; set; } = "";
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    [JsonProperty("bytes")] public long Bytes { get; set; }
    [JsonProperty("fromCache")] public bool FromCache { get; set; }
    [JsonProperty("installed")] public bool Installed { get; set; }
    [JsonProperty("installedMods")] public List<StardewMod> InstalledMods { get; set; } = new();
    [JsonProperty("installationResult")] public ModInstallResult? InstallationResult { get; set; }
}

public sealed class DownloadProgress
{
    [JsonProperty("phase")] public string Phase { get; set; } = "downloading";
    [JsonProperty("itemId")] public string ItemId { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("bytes")] public long Bytes { get; set; }
    [JsonProperty("total")] public long? Total { get; set; }
    [JsonProperty("percent")] public int? Percent => Total > 0 ? (int)Math.Min(100, Bytes * 100 / Total.Value) : null;
}
