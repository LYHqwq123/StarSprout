using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout.Services;

/// <summary>Only the anonymous requests used by the communities' own public web frontends.</summary>
public sealed class CommunityCatalogService : IDisposable
{
    private const string ThreeDm = "https://mod.3dmgame.com";
    private const string SmallFire = "https://svmbbs.smallfire.cn";
    private const int PageSize = 24;
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    public bool Offline { get; set; }

    public CommunityCatalogService(string? dataDirectory = null, HttpClient? http = null)
    {
        // The host supplies its private data directory; anonymous browsing has no credentials to persist.
        if (!string.IsNullOrWhiteSpace(dataDirectory)) _ = Path.GetFullPath(dataDirectory);
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
        ownsHttp = http is null;
    }

    public CommunityState GetState() => new()
    {
        Offline = Offline,
        Sources = new()
        {
            new() { Id = "3dm", Name = "3DM Mod站", SourceUrl = ThreeDm + "/StardewValley", SupportsApi = true,
                Description = "站内检索、详情和公开 ZIP 附件；网盘与其他格式在内嵌页面处理。" },
            new() { Id = "smallfire", Name = "小火星露谷社区", SourceUrl = SmallFire, SupportsApi = true,
                Description = "匿名读取模组帖子；部分模板引用 Nexus 下载，仍需对应账号权限。" },
            new() { Id = "esous", Name = "星露谷小站", SourceUrl = "https://esous.cn/", SupportsApi = false,
                Description = "未核实公开下载 API，可在启动器内浏览原站。" }
        }
    };

    public async Task<CommunityCatalog> ListAsync(string source, string? query = null, int page = 1,
        CancellationToken ct = default)
    {
        EnsureOnline();
        source = Source(source);
        query = (query ?? "").Trim();
        if (query.Length > 120 || query.Any(char.IsControl)) throw new ArgumentException("搜索关键词过长或包含控制字符。");
        if (page is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page), "页码应在 1 到 500 之间。");
        if (source == "3dm")
        {
            // Website path '/StardewValley/133' is a type filter. Its actual game_id is 10.
            var document = await Api(ThreeDm + "/api/search/getModlist", new
            {
                page, pageSize = PageSize, search = query, original = 0, time = 0, order = 0,
                gameType = 0, gameId = new[] { 10 }
            }, ct);
            var data = Success(document);
            var mods = data["mods"] as JArray ?? throw new InvalidDataException("社区未返回模组列表。");
            var result = new CommunityCatalog { Page = page };
            foreach (var raw in mods.OfType<JObject>().Take(PageSize))
            {
                if (Integer(raw["game_id"]) != 10) continue;
                var item = ParseThreeDm(raw);
                if (item.Id.Length > 0) result.Items.Add(item);
            }
            result.HasMore = Integer(data["count"]) > (long)page * PageSize;
            result.Warnings.Add("使用社区网页的公开接口；附件以详情页提供的版本和前置要求为准。");
            return result;
        }
        else
        {
            var url = SmallFire + "/api/discussions?page%5Blimit%5D=" + PageSize +
                      "&page%5Boffset%5D=" + ((page - 1) * PageSize).ToString(CultureInfo.InvariantCulture);
            if (query.Length > 0) url += "&filter%5Bq%5D=" + Uri.EscapeDataString(query);
            var document = await Api(url, null, ct);
            var discussions = document["data"] as JArray ?? throw new InvalidDataException("社区未返回帖子列表。");
            var included = Resources(document);
            var result = new CommunityCatalog { Page = page, HasMore = document["links"]?["next"]?.Type == JTokenType.String };
            foreach (var raw in discussions.OfType<JObject>().Take(PageSize))
            {
                var item = ParseSmallFire(raw, included);
                if (item.Id.Length > 0) result.Items.Add(item);
            }
            result.Warnings.Add("小火提供帖子检索；模板常引用 Nexus 下载地址，帖子不是完整 ZIP 整合包。");
            return result;
        }
    }

    public async Task<CommunityMod> GetDetailAsync(string source, string id, CancellationToken ct = default)
    {
        EnsureOnline();
        source = Source(source);
        var number = PositiveId(id);
        if (source == "3dm")
        {
            var document = await Api(ThreeDm + "/api/mods/" + number.ToString(CultureInfo.InvariantCulture), null, ct);
            var raw = Success(document) as JObject ?? throw new InvalidDataException("社区未返回模组详情。");
            if (Integer(raw["id"]) != number || Integer(raw["game_id"]) != 10)
                throw new InvalidDataException("该社区资源不是星露谷模组。");
            var item = ParseThreeDm(raw);
            item.Description = PlainText(Text(raw["mods_content"], 60_000), 40_000);
            if (item.Description.Length == 0) item.Description = item.Summary;
            foreach (var resource in (raw["mods_resource"] as JArray ?? new()).OfType<JObject>().Take(100))
            {
                var link = PublicHttps(Text(resource["mods_resource_url"], 4096));
                if (link.Length == 0) continue;
                var direct = IsHostedZip(link);
                item.Downloads.Add(new()
                {
                    Name = PlainText(Text(resource["mods_resource_name"], 512), 512), Url = link, CanInstall = direct
                });
                if (direct && item.DownloadUrl.Length == 0) item.DownloadUrl = link;
            }
            if (item.Downloads.Any(d => !d.CanInstall))
                item.Warnings.Add("网盘、网页或非 ZIP 附件需在内嵌原站中处理，启动器不会把网页当模组安装。");
            if (item.Downloads.All(d => !d.CanInstall))
                item.Warnings.Add("此资源没有可直接安装的公开 ZIP 附件。");
            return item;
        }
        else
        {
            var document = await Api(SmallFire + "/api/discussions/" + number.ToString(CultureInfo.InvariantCulture), null, ct);
            var raw = document["data"] as JObject ?? throw new InvalidDataException("社区未返回帖子详情。");
            if (Integer(raw["id"]) != number) throw new InvalidDataException("社区帖子 ID 不匹配。");
            var included = Resources(document);
            var item = ParseSmallFire(raw, included);
            var postId = Text(raw["relationships"]?["firstPost"]?["data"]?["id"], 32);
            JObject? firstPost = null;
            if (postId.Length > 0) included.TryGetValue("posts:" + postId, out firstPost);
            firstPost ??= included.Values.FirstOrDefault(p => Text(p["type"], 32) == "posts" && Integer(p["attributes"]?["number"]) == 1);
            var html = Text(firstPost?["attributes"]?["contentHtml"], 120_000);
            item.Description = PlainText(html, 40_000);
            item.Summary = item.Description.Length > 300 ? item.Description[..300] + "…" : item.Description;
            foreach (Match match in Regex.Matches(html, "href\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                var link = PublicHttps(WebUtility.HtmlDecode(match.Groups["url"].Value));
                if (link.Length == 0 || item.Downloads.Any(d => d.Url == link)) continue;
                var uri = new Uri(link);
                if (IsHostedZip(link) || uri.IdnHost.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase))
                {
                    item.Downloads.Add(new() { Name = IsHostedZip(link) ? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) : "Nexus 模组页面",
                        Url = link, CanInstall = IsHostedZip(link) });
                    if (IsHostedZip(link) && item.DownloadUrl.Length == 0) item.DownloadUrl = link;
                }
                if (item.Downloads.Count >= 100) break;
            }
            if (item.DownloadUrl.Length == 0)
                item.Warnings.Add("本帖未提供可直接安装的公开 ZIP，Nexus 链接需账号及下载权限；可在启动器内查看原帖。");
            return item;
        }
    }

    private async Task<JObject> Api(string url, object? body, CancellationToken ct)
    {
        // Every endpoint is constructed locally. No user-supplied URL, cookie, or key is sent.
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("StarSprout/0.4.3");
        if (body is not null) request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("社区要求登录或拒绝接口访问，请在启动器内浏览原站。");
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("社区接口发生跳转，请在启动器内浏览原站。");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("社区请求失败（HTTP " + (int)response.StatusCode + "）。");
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("社区响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > MaxResponseBytes) throw new InvalidDataException("社区响应过大。");
            output.Write(buffer, 0, count);
        }
        try
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(output.ToArray()))) { MaxDepth = 64 };
            return JObject.Load(reader);
        }
        catch (JsonException) { throw new InvalidDataException("社区返回了网页或不支持的数据，请在启动器内查看原站。"); }
    }

    private static JToken Success(JObject doc)
    {
        if (doc["success"]?.Type != JTokenType.Boolean || doc["success"]!.Value<bool>() != true || doc["data"] is null || doc["data"]!.Type == JTokenType.Null)
            throw new InvalidDataException("社区未能返回资源，接口可能暂时不可用；可在启动器内浏览原站。");
        return doc["data"]!;
    }

    private static CommunityMod ParseThreeDm(JObject raw)
    {
        var id = Integer(raw["id"]);
        if (id is < 1 or > int.MaxValue) return new();
        var item = new CommunityMod
        {
            Id = id.ToString(CultureInfo.InvariantCulture), Source = "3dm",
            Name = PlainText(Text(raw["mods_title"], 1024), 512), Summary = PlainText(Text(raw["mods_desc"], 3000), 2000),
            Author = PlainText(Text(raw["mods_author"], 256), 256), Version = PlainText(Text(raw["mods_version"], 64), 64),
            SourceUrl = ThreeDm + "/mod/" + id.ToString(CultureInfo.InvariantCulture)
        };
        if (item.Author.Length == 0) item.Author = PlainText(Text(raw["user_nickName"], 256), 256);
        var thumbnail = Text(raw["mods_image_url"], 4096);
        if (thumbnail.Length > 0 && Uri.TryCreate(new Uri(ThreeDm), thumbnail, out var image) && image.IdnHost == "mod.3dmgame.com")
            item.ThumbnailUrl = PublicHttps(image.AbsoluteUri);
        return item;
    }

    private static CommunityMod ParseSmallFire(JObject raw, Dictionary<string, JObject> resources)
    {
        var id = Integer(raw["id"]);
        if (id is < 1 or > int.MaxValue) return new();
        var userId = Text(raw["relationships"]?["user"]?["data"]?["id"], 32);
        resources.TryGetValue("users:" + userId, out var user);
        return new()
        {
            Id = id.ToString(CultureInfo.InvariantCulture), Source = "smallfire",
            Name = PlainText(Text(raw["attributes"]?["title"], 1024), 512),
            Author = PlainText(Text(user?["attributes"]?["displayName"] ?? user?["attributes"]?["username"], 256), 256),
            Summary = "小火社区模组帖子 · 查看详情了解版本、前置与下载方式。",
            SourceUrl = SmallFire + "/d/" + id.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static Dictionary<string, JObject> Resources(JObject document)
    {
        var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (var resource in (document["included"] as JArray ?? new()).OfType<JObject>().Take(2000))
        {
            var type = Text(resource["type"], 32);
            var id = Text(resource["id"], 32);
            if (type.Length > 0 && id.Length > 0) result[type + ":" + id] = resource;
        }
        return result;
    }

    private static string Source(string value) => value switch
    {
        "3dm" => "3dm", "smallfire" => "smallfire",
        "esous" => throw new InvalidOperationException("星露谷小站尚未核实公开 API，请在启动器内浏览原站。"),
        _ => throw new ArgumentException("不支持的模组社区。")
    };

    private static long PositiveId(string id)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > int.MaxValue)
            throw new ArgumentException("资源 ID 应为正整数。");
        return parsed;
    }

    private static long Integer(JToken? value) => long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static string Text(JToken? value, int limit)
    {
        if (value?.Type != JTokenType.String) return "";
        var text = value.Value<string>() ?? "";
        return text.Length > limit ? text[..limit] : text;
    }

    private static string PlainText(string value, int limit)
    {
        value = Regex.Replace(value, @"<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        value = Regex.Replace(value, @"</?(p|div|h[1-6]|li|pre|br)\b[^>]*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        value = Regex.Replace(value, @"<[^>]{0,4096}>", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        value = WebUtility.HtmlDecode(value).Replace("\r\n", "\n");
        value = Regex.Replace(value, @"\n[ \t]*\n(?:[ \t]*\n)+", "\n\n", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        value = new string(value.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
        return value.Length > limit ? value[..limit] : value;
    }

    private static string PublicHttps(string value)
    {
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns ||
            !uri.IdnHost.Contains('.') || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return "";
        return uri.AbsoluteUri;
    }

    private static bool IsHostedZip(string value)
    {
        if (PublicHttps(value).Length == 0) return false;
        var uri = new Uri(value);
        // These are the public attachment hosts verified in the 3DM website's own detail JSON.
        return (uri.IdnHost == "dmod.3dmgame.com" || uri.IdnHost == "mod.3dmgame.com") &&
               uri.AbsolutePath.StartsWith("/static/upload/", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureOnline()
    {
        if (Offline) throw new InvalidOperationException("离线模式无法检索在线社区；已缓存 ZIP 仍可安装。");
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
