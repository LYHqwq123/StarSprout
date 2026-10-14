namespace StarSprout.Services;

/// <summary>A website login can download without granting the launcher a separate API credential.</summary>
public sealed class NexusBrowserDownloadRequiredException : InvalidOperationException
{
    public Uri PageUrl { get; }
    internal NexusBrowserDownloadRequiredException(Uri pageUrl)
        : base("网页登录与 API 连接相互独立。正在软件内打开此文件的官方下载页；选择 Manual Download / Slow Download 后，ZIP 会自动缓存和安装。")
        => PageUrl = pageUrl;
}

public sealed class NexusState
{
    public bool Connected { get; set; }
    public string Username { get; set; } = "";
    public bool IsPremium { get; set; }
    public string ApplicationId { get; set; } = "";
    public bool HasApiKey { get; set; }
}
public sealed class NexusMod
{
    public long ModId { get; set; }
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public List<NexusFile> Files { get; set; } = new();
}
public sealed class NexusFile
{
    public long FileId { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public long Size { get; set; }
    public bool CanDownload { get; set; }
}
