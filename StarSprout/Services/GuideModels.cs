namespace StarSprout.Services;

public sealed class GuideListItem
{
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SourceUrl { get; set; } = "";
}

public sealed class GuideSearchResult
{
    public string Query { get; set; } = "";
    public int Page { get; set; } = 1;
    public long Total { get; set; }
    public bool HasMore { get; set; }
    public bool FromCache { get; set; }
    public string Notice { get; set; } = "";
    public List<GuideListItem> Items { get; set; } = new();
}

public sealed class GuideSection
{
    public string Title { get; set; } = "";
    public string Anchor { get; set; } = "";
    public int Level { get; set; }
}

public sealed class GuideArticle
{
    public string Title { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Html { get; set; } = "";
    public List<GuideSection> Sections { get; set; } = new();
    public string LastUpdated { get; set; } = "";
    public string CachedAt { get; set; } = "";
    public string License { get; set; } = "CC BY-NC-SA 3.0（个别素材另有声明）";
    public string LicenseUrl { get; set; } = "https://creativecommons.org/licenses/by-nc-sa/3.0/deed.zh-hans";
    public bool FromCache { get; set; }
    public string Notice { get; set; } = "";
}

public sealed class GuideState
{
    public List<GuideListItem> Favorites { get; set; } = new();
    public List<GuideListItem> Recent { get; set; } = new();
    public int CacheCount { get; set; }
}
