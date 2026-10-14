namespace StarSprout.Services;

/// <summary>A website login can download without granting the launcher a separate API credential.</summary>
public sealed class NexusBrowserDownloadRequiredException : InvalidOperationException
{
    public Uri PageUrl { get; }
    internal NexusBrowserDownloadRequiredException(Uri pageUrl)
        : base("此下载需要连接你的 Nexus API Key。也可在默认浏览器打开此文件的官方下载页，下载后导入 ZIP。")
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
    public string SourceUrl { get; set; } = "";
    public string PictureUrl { get; set; } = "";
    public long Downloads { get; set; }
    public long Endorsements { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Notice { get; set; } = "";
    public List<NexusFile> Files { get; set; } = new();
}
public sealed class NexusCatalog
{
    public string Category { get; set; } = "latest_added";
    public string Query { get; set; } = "";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 24;
    public long Total { get; set; }
    public bool HasMore { get; set; }
    public string Notice { get; set; } = "";
    public List<NexusMod> Items { get; set; } = new();
}
public sealed class NexusFile
{
    public long FileId { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Description { get; set; } = "";
    public long Size { get; set; }
    public bool IsZip { get; set; }
    public bool Available { get; set; }
    public bool CanDownload { get; set; }
}
