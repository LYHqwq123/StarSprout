using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout.Services;

/// <summary>Read-only, anonymous BWIKI adapter. Only explicitly requested pages/searches are cached.</summary>
public sealed class GuideService : IDisposable
{
    public const string WikiRoot = "https://wiki.biligame.com/stardewvalley/";
    private const string ApiEndpoint = WikiRoot + "api.php";
    private const int PageSize = 20;
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxHtmlCharacters = 3 * 1024 * 1024;
    private const int MaxCacheBytes = 64 * 1024 * 1024;
    private const int MaxArticles = 40;
    private const int MaxSearches = 24;
    private static readonly string[] Featured = { "农场", "耕种", "钓鱼", "居民", "社区中心", "矿井", "打造", "烹饪", "动物", "节日", "技能", "农作物" };
    private static readonly Regex Tags = new(@"<!--[\s\S]*?-->|<![^>]*>|<\s*(/?)\s*([A-Za-z][A-Za-z0-9:-]*)\b([^>]*?)>", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Attributes = new("([A-Za-z_:][A-Za-z0-9_.:-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'=<>`]+))", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly HashSet<string> AllowedTags = new("p div span section article h1 h2 h3 h4 h5 h6 ul ol li pre code blockquote table tbody thead tfoot tr th td caption b u strong em i small sup sub br hr img a dl dt dd figure figcaption s del".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> OmittedTags = new("script style iframe object embed svg math template noscript form button input textarea select option".Split(' '), StringComparer.Ordinal);
    private readonly string directory;
    private readonly string statePath;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly object sync = new();
    private StoredState stored = new();
    public bool Offline { get; set; }

    public GuideService(string dataDirectory, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        directory = Path.GetFullPath(dataDirectory);
        NoLinks(directory);
        Directory.CreateDirectory(directory);
        statePath = Path.Combine(directory, "reading-state.json");
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        ownsHttp = http is null;
        stored = ReadJson<StoredState>(statePath, 256 * 1024) ?? new();
        stored.Favorites = CleanTitles(stored.Favorites, 100);
        stored.Recent = CleanTitles(stored.Recent, 24);
        stored.Aliases = (stored.Aliases ?? new()).Where(pair => ValidTitle(pair.Key) && ValidTitle(pair.Value))
            .Take(200).GroupBy(pair => NormalizeTitle(pair.Key), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => NormalizeTitle(group.Last().Value), StringComparer.Ordinal);
    }

    public GuideState GetState()
    {
        lock (sync) return new()
        {
            Favorites = stored.Favorites.Select(ListItem).ToList(),
            Recent = stored.Recent.Select(ListItem).ToList(),
            CacheCount = CacheFiles("article-").Count()
        };
    }

    public GuideState SetFavorite(string title, bool favorite)
    {
        title = NormalizeTitle(title);
        lock (sync)
        {
            var next = stored.Favorites.Where(item => item != title).ToList();
            if (favorite)
            {
                if (next.Count >= 100) throw new InvalidOperationException("最多收藏 100 篇攻略，请先取消部分收藏。");
                next.Insert(0, title);
            }
            var nextState = new StoredState { Favorites = next, Recent = stored.Recent.ToList(), Aliases = new(stored.Aliases) };
            WriteJson(statePath, nextState);
            stored = nextState;
            return GetState();
        }
    }

    public async Task<GuideSearchResult> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (query.Length > 120 || query.Any(char.IsControl)) throw new ArgumentException("攻略搜索词最多 120 字，不能包含控制字符。");
        if (page is < 1 or > 100) throw new ArgumentException("攻略页码必须在 1 到 100 之间。");
        ct.ThrowIfCancellationRequested();
        if (query.Length == 0)
            return new() { Page = page, Total = Featured.Length, Items = page == 1 ? Featured.Select(ListItem).ToList() : new(), Notice = "常用百科条目 · 来源：星露谷 BWIKI" };
        string cachePath = CachePath("search-", query + "\n" + page);
        var cache = ReadJson<Cached<GuideSearchResult>>(cachePath, 1024 * 1024);
        if (cache?.Value != null && (Offline || IsFresh(cache.CachedAt, TimeSpan.FromMinutes(30))))
            return SearchFromCache(cache.Value, Offline ? "离线搜索缓存；结果可能不是最新内容。" : "");
        if (Offline) throw new InvalidOperationException("离线模式没有这次搜索的缓存；可打开最近阅读或收藏中已缓存的文章。");
        try
        {
            var json = await RequestAsync(new() { ["action"] = "query", ["list"] = "search", ["srsearch"] = query, ["srnamespace"] = "0", ["srlimit"] = PageSize.ToString(CultureInfo.InvariantCulture), ["sroffset"] = ((page - 1) * PageSize).ToString(CultureInfo.InvariantCulture), ["srprop"] = "snippet" }, ct);
            var results = json["query"]?["search"] as JArray ?? throw new InvalidDataException("BWIKI 没有返回有效的搜索结果。");
            var value = new GuideSearchResult
            {
                Query = query, Page = page,
                Total = Math.Max(0, json["query"]?["searchinfo"]?.Value<long?>("totalhits") ?? 0),
                HasMore = json["continue"]?["sroffset"] != null,
                Items = results.Take(PageSize).Select(item => new GuideListItem { Title = NormalizeTitle(item.Value<string>("title") ?? ""), Summary = Plain(item.Value<string>("snippet") ?? "", 600) }).ToList()
            };
            foreach (var item in value.Items) item.SourceUrl = SourceUrl(item.Title);
            TryCache(cachePath, new Cached<GuideSearchResult> { CachedAt = DateTimeOffset.UtcNow, Value = value });
            return value;
        }
        catch (Exception ex) when (CanUseCache(ex, ct) && cache?.Value != null)
        { return SearchFromCache(cache.Value, "网络暂不可用，正在显示上次搜索缓存。"); }
    }

    public async Task<GuideArticle> GetArticleAsync(string title, CancellationToken ct = default)
    {
        title = NormalizeTitle(title);
        ct.ThrowIfCancellationRequested();
        string cacheTitle;
        lock (sync) cacheTitle = stored.Aliases.GetValueOrDefault(title, title);
        string cachePath = CachePath("article-", cacheTitle);
        var cache = ReadJson<Cached<GuideArticle>>(cachePath, MaxResponseBytes);
        GuideArticle result;
        if (cache?.Value != null && (Offline || IsFresh(cache.CachedAt, TimeSpan.FromHours(6))))
            result = ArticleFromCache(cache.Value, Offline ? "离线缓存正文；未加载过的图片仍需联网。" : "");
        else
        {
            if (Offline) throw new InvalidOperationException("这篇攻略尚未缓存。请联网阅读一次后，再使用离线阅读。");
            try
            {
                var json = await RequestAsync(new() { ["action"] = "parse", ["page"] = title, ["prop"] = "text|sections|revid", ["redirects"] = "1", ["disableeditsection"] = "1", ["disabletoc"] = "1" }, ct);
                var parsed = json["parse"] ?? throw new InvalidDataException("BWIKI 没有返回有效的文章正文。");
                string rawHtml = parsed["text"]?.Type == JTokenType.String ? parsed.Value<string>("text") ?? "" : parsed["text"]?.Value<string>("*") ?? "";
                if (rawHtml.Length > MaxHtmlCharacters) throw new InvalidDataException("这篇攻略过长，请使用原文链接查看。");
                result = new()
                {
                    Title = NormalizeTitle(parsed.Value<string>("title") ?? title), Html = SanitizeHtml(rawHtml),
                    CachedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Sections = (parsed["sections"] as JArray ?? new()).Take(300).Select(section => new GuideSection { Title = Plain(section.Value<string>("line") ?? "", 200), Anchor = SafeAnchor(section.Value<string>("anchor") ?? ""), Level = Math.Clamp(section.Value<int?>("level") ?? 2, 1, 6) }).Where(section => section.Anchor.Length > 0).ToList()
                };
                result.SourceUrl = SourceUrl(result.Title);
                // LastUpdated is a revision timestamp, never the local cache time.
                try
                {
                    var meta = await RequestAsync(new() { ["action"] = "query", ["titles"] = result.Title, ["prop"] = "revisions", ["rvprop"] = "timestamp", ["rvlimit"] = "1" }, ct);
                    var pages = meta["query"]?["pages"];
                    var first = pages is JArray array ? array.FirstOrDefault() : (pages as JObject)?.Properties().FirstOrDefault()?.Value;
                    result.LastUpdated = first?["revisions"]?.FirstOrDefault()?.Value<string>("timestamp") ?? "";
                }
                catch (Exception ex) when (CanUseCache(ex, ct)) { }
                TryCache(CachePath("article-", result.Title), new Cached<GuideArticle> { CachedAt = DateTimeOffset.UtcNow, Value = result });
            }
            catch (Exception ex) when (CanUseCache(ex, ct) && cache?.Value != null)
            { result = ArticleFromCache(cache.Value, "网络暂不可用，正在显示上次阅读缓存；图片可能需要联网。"); }
        }
        // BWIKI redirects (e.g. 村民 -> 居民) must also work when a canonical-title favorite is opened offline.
        if (title != result.Title)
        {
            string canonicalPath = CachePath("article-", result.Title);
            if (!File.Exists(canonicalPath))
                TryCache(canonicalPath, new Cached<GuideArticle> { CachedAt = DateTimeOffset.TryParse(result.CachedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var cachedAt) ? cachedAt : DateTimeOffset.UtcNow, Value = result });
            if (cacheTitle == title && cachePath != canonicalPath && File.Exists(canonicalPath))
                try { NoLinks(cachePath); File.Delete(cachePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        RecordRecent(result.Title, title);
        return result;
    }

    /// <summary>Rebuild markup from a small allowlist. Reader must also reconstruct safe DOM nodes.</summary>
    public static string SanitizeHtml(string html)
    {
        if (html.Length > MaxHtmlCharacters) throw new InvalidDataException("攻略正文超过上限。");
        var output = new StringBuilder(Math.Min(html.Length, 256 * 1024));
        int offset = 0;
        var omitted = new Stack<string>();
        foreach (Match match in Tags.Matches(html))
        {
            if (omitted.Count == 0) output.Append(WebUtility.HtmlEncode(WebUtility.HtmlDecode(html[offset..match.Index])));
            offset = match.Index + match.Length;
            if (!match.Groups[2].Success) continue;
            string tag = match.Groups[2].Value.ToLowerInvariant();
            bool closing = match.Groups[1].Value == "/";
            if (OmittedTags.Contains(tag))
            {
                if (!closing && tag is not ("embed" or "input")) omitted.Push(tag);
                else if (closing && omitted.Count > 0 && omitted.Peek() == tag) omitted.Pop();
                continue;
            }
            if (omitted.Count > 0 || !AllowedTags.Contains(tag)) continue;
            if (closing)
            {
                if (tag is not ("img" or "br" or "hr")) output.Append("</").Append(tag).Append('>');
                continue;
            }
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in Attributes.Matches(match.Groups[3].Value))
            {
                string name = attribute.Groups[1].Value.ToLowerInvariant();
                if (attrs.ContainsKey(name)) continue;
                string value = WebUtility.HtmlDecode(attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value);
                attrs[name] = value;
            }
            var selected = new Dictionary<string, string>();
            if (attrs.TryGetValue("id", out var id) && SafeAnchor(id).Length > 0) selected["id"] = SafeAnchor(id);
            if (attrs.TryGetValue("title", out var tooltip)) selected["title"] = tooltip[..Math.Min(tooltip.Length, 300)];
            if (tag == "a" && attrs.TryGetValue("href", out var href))
            {
                string link = SafeLink(href);
                if (link.Length > 0) selected["href"] = link;
            }
            if (tag == "img")
            {
                string image = attrs.TryGetValue("src", out var src) ? SafeImageUrl(src) : "";
                if (image.Length == 0) continue;
                selected["src"] = image;
                selected["alt"] = attrs.TryGetValue("alt", out var alt) ? alt[..Math.Min(alt.Length, 400)] : "";
                selected["loading"] = "lazy";
                selected["referrerpolicy"] = "no-referrer";
                foreach (string dimension in new[] { "width", "height" })
                    if (attrs.TryGetValue(dimension, out var dimensionText) && int.TryParse(dimensionText, out int value) && value is > 0 and <= 8000) selected[dimension] = value.ToString(CultureInfo.InvariantCulture);
            }
            if (tag is "td" or "th")
                foreach (string span in new[] { "colspan", "rowspan" })
                    if (attrs.TryGetValue(span, out var spanText) && int.TryParse(spanText, out int value) && value is > 0 and <= 100) selected[span] = value.ToString(CultureInfo.InvariantCulture);
            output.Append('<').Append(tag);
            foreach (var attribute in selected) output.Append(' ').Append(attribute.Key).Append("=\"").Append(WebUtility.HtmlEncode(attribute.Value)).Append('"');
            output.Append('>');
        }
        if (omitted.Count == 0) output.Append(WebUtility.HtmlEncode(WebUtility.HtmlDecode(html[offset..])));
        return output.ToString();
    }

    public static string SafeImageUrl(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !PublicHttps(uri) || !uri.Host.Equals("patchwiki.biligame.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith("/images/stardewvalley/", StringComparison.Ordinal) || uri.Query.Length > 0) return "";
        return uri.AbsoluteUri;
    }

    public static string NormalizeTitle(string title)
    {
        title = (title ?? "").Trim();
        if (title.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(title, UriKind.Absolute, out var uri) || !PublicHttps(uri) || uri.Host != "wiki.biligame.com" || !uri.AbsolutePath.StartsWith("/stardewvalley/", StringComparison.Ordinal) || uri.Query.Length > 0) throw new ArgumentException("仅支持星露谷 BWIKI 的文章链接。");
            title = Uri.UnescapeDataString(uri.AbsolutePath["/stardewvalley/".Length..]).Replace('_', ' ');
        }
        if (title.Length is < 1 or > 255 || title.Any(char.IsControl) || title.IndexOfAny("<>[]{}|\\".ToCharArray()) >= 0 || title is "." or ".." || title.Equals("api.php", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请填写有效的 BWIKI 文章标题。");
        return title.Replace('_', ' ');
    }

    public static string SourceUrl(string title) => WikiRoot + Uri.EscapeDataString(NormalizeTitle(title).Replace(' ', '_'));

    private static string SafeLink(string value)
    {
        if (value.StartsWith('#')) return SafeAnchor(value[1..]).Length > 0 ? "#" + SafeAnchor(value[1..]) : "";
        if (!Uri.TryCreate(new Uri(WikiRoot), value, out var uri) || !PublicHttps(uri) || uri.Host != "wiki.biligame.com" || !uri.AbsolutePath.StartsWith("/stardewvalley/", StringComparison.Ordinal) || uri.Query.Length > 0) return "";
        try { return SourceUrl(NormalizeTitle(uri.GetLeftPart(UriPartial.Path))) + (uri.Fragment.Length > 1 ? "#" + Uri.EscapeDataString(SafeAnchor(Uri.UnescapeDataString(uri.Fragment[1..]))) : ""); }
        catch (ArgumentException) { return ""; }
    }
    private static bool PublicHttps(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    private static string SafeAnchor(string value) => value.Length <= 300 && !value.Any(char.IsControl) && value.IndexOfAny("<>\"'`".ToCharArray()) < 0 ? value : "";
    private static GuideListItem ListItem(string title) => new() { Title = title, SourceUrl = SourceUrl(title) };
    private static string Plain(string value, int limit)
    {
        value = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        return value[..Math.Min(value.Length, limit)];
    }

    private async Task<JObject> RequestAsync(Dictionary<string, string> query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Offline) throw new InvalidOperationException("离线模式不会请求攻略网站。");
        query["format"] = "json";
        query["formatversion"] = "2";
        var uri = new Uri(ApiEndpoint + "?" + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("StarSprout/0.4.3 (+https://github.com/LYHqwq123/StarSprout; guide-reader)");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect || response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path) is string final && final != ApiEndpoint)
            throw new HttpRequestException("BWIKI API 发生重定向，请稍后重试或打开原文。");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"BWIKI 暂时无法访问（HTTP {(int)response.StatusCode}）。");
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("BWIKI 响应超过大小上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[32 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw new InvalidDataException("BWIKI 响应超过大小上限。");
            buffer.Write(chunk, 0, count);
        }
        JObject json;
        try
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(buffer.ToArray()))) { DateParseHandling = DateParseHandling.None };
            json = JObject.Load(reader);
        }
        catch (JsonException) { throw new InvalidDataException("BWIKI 返回了无效内容，可能暂时无法访问。"); }
        if (json["error"] is JToken error) throw new InvalidDataException("BWIKI：" + Plain(error.Value<string>("info") ?? "文章不存在或接口暂不可用。", 200));
        return json;
    }

    private GuideSearchResult SearchFromCache(GuideSearchResult value, string notice)
    {
        value.Items = (value.Items ?? new()).Take(PageSize).Where(item => ValidTitle(item.Title)).Select(item => new GuideListItem { Title = NormalizeTitle(item.Title), Summary = Plain(item.Summary ?? "", 600), SourceUrl = SourceUrl(item.Title) }).ToList();
        value.FromCache = true; value.Notice = notice; return value;
    }
    private static GuideArticle ArticleFromCache(GuideArticle value, string notice)
    {
        value.Title = NormalizeTitle(value.Title); value.SourceUrl = SourceUrl(value.Title); value.Html = SanitizeHtml(value.Html ?? "");
        value.Sections = (value.Sections ?? new()).Take(300).Where(section => SafeAnchor(section.Anchor).Length > 0).Select(section => new GuideSection { Title = Plain(section.Title ?? "", 200), Anchor = SafeAnchor(section.Anchor), Level = Math.Clamp(section.Level, 1, 6) }).ToList();
        value.License = new GuideArticle().License; value.LicenseUrl = new GuideArticle().LicenseUrl;
        value.FromCache = true; value.Notice = notice; return value;
    }
    private void RecordRecent(string title, string requestedTitle)
    {
        lock (sync)
        {
            var next = new StoredState { Favorites = stored.Favorites.ToList(), Recent = new[] { title }.Concat(stored.Recent.Where(item => item != title)).Take(24).ToList(), Aliases = new(stored.Aliases) };
            if (requestedTitle != title)
            {
                next.Aliases.Remove(requestedTitle);
                next.Aliases[requestedTitle] = title;
                next.Aliases = next.Aliases.TakeLast(200).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }
            try { WriteJson(statePath, next); stored = next; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static bool CanUseCache(Exception exception, CancellationToken ct) => !ct.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException;
    private static bool IsFresh(DateTimeOffset timestamp, TimeSpan lifetime) => timestamp <= DateTimeOffset.UtcNow && timestamp > DateTimeOffset.UtcNow - lifetime;
    private string CachePath(string prefix, string key) => Path.Combine(directory, prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json");
    private static bool ValidTitle(string? value) { try { _ = NormalizeTitle(value ?? ""); return true; } catch (ArgumentException) { return false; } }
    private static List<string> CleanTitles(List<string>? values, int limit) => (values ?? new()).Where(ValidTitle).Select(NormalizeTitle).Distinct(StringComparer.Ordinal).Take(limit).ToList();
    private IEnumerable<FileInfo> CacheFiles(string prefix)
    {
        NoLinks(directory);
        return Directory.EnumerateFiles(directory, prefix + "*.json", SearchOption.TopDirectoryOnly).Select(path => new FileInfo(path)).Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0);
    }
    private void TryCache<T>(string path, T value)
    {
        lock (sync)
        {
            try
            {
                WriteJson(path, value);
                foreach (string prefix in new[] { "article-", "search-" })
                {
                    int limit = prefix == "article-" ? MaxArticles : MaxSearches;
                    foreach (var stale in CacheFiles(prefix).OrderByDescending(file => file.LastWriteTimeUtc).Skip(limit)) { NoLinks(stale.FullName); stale.Delete(); }
                }
                var files = CacheFiles("article-").Concat(CacheFiles("search-")).OrderBy(file => file.LastWriteTimeUtc).ToList();
                long size = files.Sum(file => file.Length);
                foreach (var stale in files)
                {
                    if (size <= MaxCacheBytes) break;
                    NoLinks(stale.FullName); long length = stale.Length; stale.Delete(); size -= length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
    private static T? ReadJson<T>(string path, int maxBytes)
    {
        NoLinks(path);
        if (!File.Exists(path)) return default;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maxBytes) return default;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }
    private static void WriteJson<T>(string path, T value)
    {
        NoLinks(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".guide-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
            if (bytes.Length > MaxResponseBytes) throw new IOException("攻略缓存超过大小上限。");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            NoLinks(path); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("攻略缓存目录不能包含符号链接或目录联接。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    public void Dispose() { if (ownsHttp) http.Dispose(); }
    private sealed class StoredState { public List<string> Favorites { get; set; } = new(); public List<string> Recent { get; set; } = new(); public Dictionary<string, string> Aliases { get; set; } = new(); }
    private sealed class Cached<T> { public DateTimeOffset CachedAt { get; set; } public T? Value { get; set; } }
}
