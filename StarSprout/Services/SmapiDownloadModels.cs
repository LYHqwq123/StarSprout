using Newtonsoft.Json;

namespace StarSprout.Services;

public sealed class SmapiDownloadSource
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("sourceUrl")] public string SourceUrl { get; set; } = "";
    [JsonProperty("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    [JsonProperty("minimumGameVersion")] public string MinimumGameVersion { get; set; } = "1.6.14";
    [JsonProperty("description")] public string Description { get; set; } = "";
}

public sealed class SmapiDownloadState
{
    [JsonProperty("offline")] public bool Offline { get; set; }
    [JsonProperty("sources")] public List<SmapiDownloadSource> Sources { get; set; } = new();
    [JsonProperty("cacheDirectory")] public string CacheDirectory { get; set; } = "";
}

public sealed class SmapiDownloadResult
{
    [JsonProperty("sourceId")] public string SourceId { get; set; } = "";
    [JsonProperty("sourceName")] public string SourceName { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("path")] public string Path { get; set; } = "";
    [JsonProperty("directory")] public string Directory { get; set; } = "";
    [JsonProperty("installerPath")] public string InstallerPath { get; set; } = "";
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    [JsonProperty("bytes")] public long Bytes { get; set; }
    [JsonProperty("fromCache")] public bool FromCache { get; set; }
    [JsonProperty("usedFallback")] public bool UsedFallback { get; set; }
    [JsonProperty("message")] public string Message { get; set; } = "";
}

public sealed class SmapiDownloadProgress
{
    [JsonProperty("phase")] public string Phase { get; set; } = "downloading";
    [JsonProperty("name")] public string Name { get; set; } = "SMAPI 安装包";
    [JsonProperty("bytes")] public long Bytes { get; set; }
    [JsonProperty("total")] public long? Total { get; set; }
    [JsonProperty("percent")] public int? Percent => Total > 0 ? (int)Math.Min(100, Bytes * 100 / Total.Value) : null;
}
