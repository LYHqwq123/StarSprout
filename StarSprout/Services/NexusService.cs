using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout.Services;

public sealed class NexusService : IDisposable
{
    // Nexus's official game ID, verified from its public game/mod API.
    private const int StardewGameId = 1303;
    private const int CatalogPageSize = 24;
    private const string ModCardFields = "modId gameId name author summary version thumbnailUrl downloads endorsements updatedAt";
    private readonly string path;
    private readonly ContentService content;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly object sync = new();
    private Stored stored = new();
    public Func<string, Task>? OpenLoginPage { get; set; }

    public NexusService(string dataDir, ContentService content, HttpClient? http = null)
    {
        path = Path.Combine(Path.GetFullPath(dataDir), "nexus-settings.json");
        this.content = content;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        ownsHttp = http is null;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("N网配置文件过大。");
            try { stored = JsonConvert.DeserializeObject<Stored>(File.ReadAllText(path)) ?? new(); }
            catch (JsonException) { stored = new(); }
        }
    }

    public NexusState GetState()
    {
        lock (sync)
        {
            bool key;
            try { key = Key().Length > 0; } catch { key = false; }
            return new() { Connected = key && stored.UserId > 0, Username = stored.Username, IsPremium = key && stored.IsPremium, ApplicationId = stored.ApplicationId, HasApiKey = key };
        }
    }

    public async Task<NexusState> SaveApiKeyAsync(string key, CancellationToken ct = default)
    {
        EnsureOnline();
        key = (key ?? "").Trim();
        if (key.Length is < 16 or > 8192 || key.Any(char.IsControl)) throw new ArgumentException("请填写有效的 N网 Personal API Key。");
        var user = await Api("users/validate.json", ct, key);
        var id = user.Value<long?>("user_id") ?? 0;
        if (id <= 0) throw new InvalidDataException("N网未返回有效账号信息。");
        var bytes = Encoding.UTF8.GetBytes(key);
        try
        {
            lock (sync)
            {
                stored.ProtectedApiKey = Convert.ToBase64String(LogAnalysisService.WindowsDpapi.Transform(bytes, true));
                stored.Username = user.Value<string>("name") ?? "Nexus用户";
                stored.UserId = id;
                stored.IsPremium = user.Value<bool?>("is_premium") ?? false;
                Save();
            }
        }
        finally { Array.Clear(bytes); }
        return GetState();
    }

    public void ClearKey()
    {
        lock (sync) { stored.ProtectedApiKey = ""; stored.Username = ""; stored.UserId = 0; stored.IsPremium = false; Save(); }
    }

    public void SaveApplicationId(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length > 0 && !Regex.IsMatch(id, @"^[A-Za-z][A-Za-z0-9_-]{1,79}$")) throw new ArgumentException("应用ID应填写N网审核通过的 application slug。");
        lock (sync) { stored.ApplicationId = id; Save(); }
    }

    public async Task<NexusState> StartSsoAsync(CancellationToken ct = default)
    {
        EnsureOnline();
        var application = GetState().ApplicationId;
        if (application.Length == 0) throw new InvalidOperationException("网页登录需要 Nexus 审核通过的应用ID。未配置时，请使用 Personal API Key 连接账号。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("wss://sso.nexusmods.com"), timeout.Token);
        var id = Guid.NewGuid().ToString();
        var request = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { id, token = (string?)null, protocol = 2 }));
        await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, timeout.Token);
        var loginUrl = $"https://www.nexusmods.com/sso?id={id}&application={Uri.EscapeDataString(application)}";
        if (OpenLoginPage is null) throw new InvalidOperationException("请从启动器内打开 Nexus 登录页面。");
        await OpenLoginPage(loginUrl);
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult message;
            do
            {
                message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (message.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("N网登录连接已关闭，请重新连接。");
                frame.Write(buffer, 0, message.Count);
                if (frame.Length > 64 * 1024) throw new InvalidDataException("N网登录响应过大。");
            } while (!message.EndOfMessage);
            var response = JObject.Parse(Encoding.UTF8.GetString(frame.ToArray()));
            if (response.Value<bool?>("success") == false) throw new InvalidOperationException("N网未批准本次登录，请核实应用ID和账号状态。");
            var apiKey = response["data"]?.Value<string>("api_key");
            if (!string.IsNullOrEmpty(apiKey)) return await SaveApiKeyAsync(apiKey, timeout.Token);
        }
        throw new InvalidOperationException("N网登录没有完成。");
    }

    /// <summary>Official v2 server-side name search and paged catalog; does not scrape a website.</summary>
    public async Task<NexusCatalog> GetCatalogAsync(string category = "latest_added", string? query = null, int page = 1, CancellationToken ct = default)
    {
        EnsureOnline();
        category = (category ?? "latest_added").Trim().ToLowerInvariant();
        if (category is not ("latest_added" or "latest_updated" or "popular" or "trending")) throw new ArgumentException("未知的 Nexus 列表分类。");
        if (page is < 1 or > 100000) throw new ArgumentException("Nexus 页码无效。");
        query = (query ?? "").Trim();
        if (query.Length > 120 || query.Any(char.IsControl)) throw new ArgumentException("搜索名称限 120 个字符，且不能包含控制字符。");
        if (category == "trending" && query.Length == 0)
        {
            if (page != 1) throw new ArgumentException("Nexus 热门趋势榜不支持翻页。");
            var response = await Api("games/stardewvalley/mods/trending.json", ct);
            if (response is not JArray rows) throw new InvalidDataException("N网榜单响应格式不正确。");
            var items = rows.OfType<JObject>().Select(row => MapRestMod(row)).Where(m => m.ModId > 0).Take(100).ToList();
            return new() { Category = category, Page = 1, PageSize = items.Count, Total = items.Count, Items = items, Notice = "Nexus 官方热门趋势榜；此榜单不支持翻页。" };
        }
        var filter = new JObject { ["gameDomainName"] = new JObject { ["value"] = "stardewvalley", ["op"] = "EQUALS" } };
        if (query.Length > 0)
            filter["nameStemmed"] = new JObject { ["value"] = query, ["op"] = "MATCHES" };
        var sortField = query.Length > 0 ? "relevance" : category switch { "latest_updated" => "updatedAt", "popular" => "downloads", _ => "createdAt" };
        var variables = new JObject
        {
            ["filter"] = filter,
            ["sort"] = new JArray(new JObject { [sortField] = new JObject { ["direction"] = "DESC" } }),
            ["count"] = CatalogPageSize,
            ["offset"] = checked((page - 1) * CatalogPageSize)
        };
        var data = await GraphApi("query StarSproutCatalog($filter: ModsFilter, $sort: [ModsSort!], $count: Int!, $offset: Int!) { mods(filter: $filter, sort: $sort, count: $count, offset: $offset) { totalCount nodes { " + ModCardFields + " } } }", variables, ct);
        if (data["mods"] is not JObject result || result["nodes"] is not JArray nodes) throw new InvalidDataException("N网目录响应格式不正确。");
        var cards = nodes.OfType<JObject>().Take(CatalogPageSize).Select(MapGraphMod).ToList();
        var total = Math.Max(result.Value<long?>("totalCount") ?? 0, 0);
        return new()
        {
            Category = category, Query = query, Page = page, PageSize = CatalogPageSize, Total = total,
            HasMore = cards.Count > 0 && (long)(page - 1) * CatalogPageSize + nodes.Count < total,
            Items = cards,
            Notice = query.Length > 0 ? "Nexus 全站星露谷 Mod 名称搜索，结果由官方 API 返回。" : "Nexus 官方星露谷 Mod 目录。"
        };
    }

    public async Task<NexusMod> GetModAsync(long modId, CancellationToken ct = default)
    {
        Positive(modId);
        if (!GetState().HasApiKey) return await GetPublicModAsync(modId, ct);
        var info = await Api($"games/stardewvalley/mods/{modId}.json", ct);
        var files = await Api($"games/stardewvalley/mods/{modId}/files.json", ct);
        var result = MapRestMod((JObject)info, modId);
        if (result.ModId != modId) throw new InvalidDataException("N网返回的 Mod 与所选资源不一致。");
        var premium = GetState().IsPremium;
        result.Files = (files["files"] as JArray ?? new()).OfType<JObject>().Select(f => MapFile(f, premium, false)).Where(f => f.FileId > 0).OrderBy(FileRank).ToList();
        return result;
    }

    private async Task<NexusMod> GetPublicModAsync(long modId, CancellationToken ct)
    {
        var variables = new JObject { ["id"] = modId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["fileModId"] = modId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var data = await GraphApi("query StarSproutMod($id: String!, $fileModId: ID!) { mods(filter: { gameId: { value: \"1303\", op: EQUALS }, modId: { value: $id, op: EQUALS } }, count: 1) { nodes { " + ModCardFields + " description } } modFiles(modId: $fileModId, gameId: \"1303\") { fileId name version category sizeInBytes uri description } }", variables, ct);
        if (data["mods"]?["nodes"] is not JArray nodes || nodes.Count == 0) throw new InvalidOperationException("N网没有找到这个星露谷 Mod，资源可能已隐藏或删除。");
        var result = MapGraphMod((JObject)nodes[0]);
        if (result.ModId != modId) throw new InvalidDataException("N网返回的 Mod 与所选资源不一致。");
        if (data["modFiles"] is not JArray files) throw new InvalidDataException("N网文件列表响应格式不正确。");
        result.Files = files.OfType<JObject>().Take(2000).Select(f => MapFile(f, false, true)).Where(f => f.FileId > 0).OrderBy(FileRank).ToList();
        result.Notice = "公开详情与文件列表可直接浏览。Premium 账户连接 API Key 后可直接下载；普通账户需在官网授权管理器下载。";
        return result;
    }

    private static NexusMod MapGraphMod(JObject info)
    {
        if (info.Value<int?>("gameId") != StardewGameId) throw new InvalidDataException("N网返回了其他游戏的资源。");
        var id = info.Value<long?>("modId") ?? 0;
        Positive(id);
        return new()
        {
            ModId = id, Name = Text(info, "name", 240), Author = Text(info, "author", 240), Summary = Text(info, "summary", 4000),
            Description = Text(info, "description", 32000), Version = Text(info, "version", 120),
            SourceUrl = $"https://www.nexusmods.com/stardewvalley/mods/{id}", PictureUrl = PublicImageUrl(Text(info, "thumbnailUrl", 2048)),
            Downloads = Math.Max(info.Value<long?>("downloads") ?? 0, 0), Endorsements = Math.Max(info.Value<long?>("endorsements") ?? 0, 0), UpdatedAt = Text(info, "updatedAt", 80)
        };
    }

    private static NexusMod MapRestMod(JObject info, long fallbackId = 0)
    {
        if (info.Value<int?>("game_id") is int game && game != StardewGameId) throw new InvalidDataException("N网返回了其他游戏的资源。");
        var id = info.Value<long?>("mod_id") ?? fallbackId;
        return new()
        {
            ModId = id, Name = Text(info, "name", 240), Author = Text(info, "author", 240), Summary = Text(info, "summary", 4000),
            Description = Text(info, "description", 32000), Version = Text(info, "version", 120),
            SourceUrl = $"https://www.nexusmods.com/stardewvalley/mods/{id}", PictureUrl = PublicImageUrl(Text(info, "picture_url", 2048)),
            Downloads = Math.Max(info.Value<long?>("mod_downloads") ?? 0, 0), Endorsements = Math.Max(info.Value<long?>("endorsement_count") ?? 0, 0), UpdatedAt = Text(info, "updated_time", 80)
        };
    }

    private static NexusFile MapFile(JObject file, bool premium, bool graph)
    {
        var category = Text(file, graph ? "category" : "category_name", 80);
        var fileName = Text(file, graph ? "uri" : "file_name", 500);
        var available = category.ToUpperInvariant() is not ("DELETED" or "ARCHIVED");
        var zip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        return new()
        {
            FileId = file.Value<long?>(graph ? "fileId" : "file_id") ?? 0, Name = Text(file, "name", 240), Version = Text(file, "version", 120),
            CategoryName = category, FileName = fileName, Description = Text(file, "description", 4000),
            Size = Math.Max(file.Value<long?>(graph ? "sizeInBytes" : "size_in_bytes") ?? (file.Value<long?>("size") ?? 0) * 1024, 0),
            IsZip = zip, Available = available, CanDownload = premium && zip && available
        };
    }
    private static int FileRank(NexusFile file) => file.CategoryName.ToUpperInvariant() switch { "MAIN" => 0, "OPTIONAL" => 1, "PATCH" => 2, "MISCELLANEOUS" => 3, "OLD_VERSION" => 4, "ARCHIVED" => 5, "DELETED" => 6, _ => 3 };
    private static string Text(JObject value, string field, int limit) { var text = value.Value<string>(field) ?? ""; return text[..Math.Min(text.Length, limit)]; }
    private static string PublicImageUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host.Equals("staticdelivery.nexusmods.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("images.nexusmods.com", StringComparison.OrdinalIgnoreCase)) ? uri.AbsoluteUri : "";

    public Task<DownloadResult> DownloadFileAsync(long modId, long fileId, bool install, CancellationToken ct = default)
    {
        if (!GetState().IsPremium) throw new InvalidOperationException("免费账号请在N网页面点击 Mod Manager Download，使用 nxm 链接授权下载；API直接获取链接需要Premium。");
        return Download(modId, fileId, null, install, ct);
    }

    public async Task<DownloadResult> HandleNxmAsync(string value, CancellationToken ct = default)
    {
        EnsureOnline();
        var parsed = ParseNxm(value);
        if (!GetState().HasApiKey)
            throw new NexusBrowserDownloadRequiredException(ManualDownloadPage(parsed.ModId, parsed.FileId));
        if (parsed.UserId > 0 && parsed.UserId != GetUserId()) throw new InvalidOperationException("此下载链接属于其他N网账号，请用当前账号重新点击管理器下载。");
        if (parsed.Expires > 0 && parsed.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new InvalidOperationException("N网下载授权已过期，请重新点击管理器下载。");
        var query = parsed.Key.Length > 0 ? $"key={Uri.EscapeDataString(parsed.Key)}&expires={parsed.Expires}" : null;
        if (!GetState().IsPremium && query is null) throw new InvalidOperationException("免费账号的nxm链接缺少临时下载授权，请从N网重新生成。");
        return await Download(parsed.ModId, parsed.FileId, query, false, ct);
    }

    internal static NxmLink ParseNxm(string value)
    {
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "nxm" || !uri.Host.Equals("stardewvalley", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0)
            throw new ArgumentException("这里只接受星露谷物语的 nxm 下载链接。");
        var match = Regex.Match(uri.AbsolutePath, @"^/mods/(\d+)/files/(\d+)/?$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException("nxm链接格式不正确。");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "", StringComparer.OrdinalIgnoreCase);
        query.TryGetValue("key", out var key);
        long.TryParse(query.GetValueOrDefault("expires"), out var expires);
        long.TryParse(query.GetValueOrDefault("user_id"), out var user);
        if (!long.TryParse(match.Groups[1].Value, out var modId) || !long.TryParse(match.Groups[2].Value, out var fileId))
            throw new ArgumentException("N网链接的 Mod / File ID 无效。");
        var result = new NxmLink(modId, fileId, key ?? "", expires, user);
        Positive(result.ModId); Positive(result.FileId);
        return result;
    }
    internal record NxmLink(long ModId, long FileId, string Key, long Expires, long UserId);

    /// <summary>Accept pasted Stardew mod URLs without forwarding credentials or unrelated query data.</summary>
    public static Uri NormalizeModPageUrl(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length is 0 or > 4096 || input.Any(char.IsControl)) throw new ArgumentException("请粘贴星露谷物语的 Nexus Mod 链接或 Mod ID。");
        if (Regex.IsMatch(input, @"^\d+$", RegexOptions.CultureInvariant))
        {
            if (!long.TryParse(input, out var id)) throw new ArgumentException("Nexus Mod ID 无效。");
            Positive(id);
            return new Uri($"https://www.nexusmods.com/stardewvalley/mods/{id}?tab=files");
        }
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            !(uri.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("这里只接受 Nexus 官方 HTTPS 星露谷 Mod 链接。");
        var match = Regex.Match(uri.AbsolutePath, @"^/(?:games/)?stardewvalley/mods/(\d+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var modId) || modId <= 0)
            throw new ArgumentException("请选择星露谷物语的 Nexus Mod 详情或文件链接。");
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            if (key is not ("tab" or "file_id" or "nmm")) continue;
            if (!parameters.TryAdd(key, pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "")) throw new ArgumentException("Nexus 链接包含重复的文件参数。");
        }
        var tab = parameters.GetValueOrDefault("tab", "files");
        if (tab is not ("files" or "description" or "images" or "videos" or "posts" or "bugs" or "logs" or "articles" or "stats"))
            throw new ArgumentException("Nexus 页面选项无效。");
        var query = "tab=" + tab;
        if (parameters.TryGetValue("file_id", out var file))
        {
            if (!long.TryParse(file, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var fileId) || fileId <= 0)
                throw new ArgumentException("Nexus 文件 ID 无效。");
            query = "tab=files&file_id=" + fileId;
            if (parameters.TryGetValue("nmm", out var nmm))
            {
                if (nmm is not ("0" or "1")) throw new ArgumentException("Nexus 下载方式无效。");
                query += "&nmm=" + nmm;
            }
        }
        else if (parameters.ContainsKey("nmm")) throw new ArgumentException("Nexus 下载链接缺少文件 ID。");
        return new Uri($"https://www.nexusmods.com/stardewvalley/mods/{modId}?{query}");
    }

    internal static Uri ManualDownloadPage(long modId, long fileId)
    {
        Positive(modId); Positive(fileId);
        return new Uri($"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&file_id={fileId}&nmm=0");
    }

    private async Task<DownloadResult> Download(long modId, long fileId, string? query, bool install, CancellationToken ct)
    {
        Positive(modId); Positive(fileId);
        var response = await Api($"games/stardewvalley/mods/{modId}/files/{fileId}/download_link.json" + (query is null ? "" : "?" + query), ct);
        var link = response is JArray links ? links.OfType<JObject>().Select(l => l.Value<string>("URI")).FirstOrDefault(u => u?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true) : null;
        if (link is null) throw new InvalidDataException("N网没有返回 HTTPS 下载链接。");
        return await content.DownloadUrlAsync(link, null, install, ct, sourcePageUrl: $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files");
    }

    private async Task<JToken> Api(string relative, CancellationToken ct, string? overrideKey = null)
    {
        EnsureOnline();
        var key = overrideKey ?? Key();
        if (key.Length == 0) throw new InvalidOperationException("请先登录N网或保存 Personal API Key。");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nexusmods.com/v1/" + relative);
        request.Headers.Add("apikey", key);
        AddApplicationHeaders(request);
        return await SendApiRequest(request, ct);
    }

    private async Task<JObject> GraphApi(string query, JObject variables, CancellationToken ct)
    {
        EnsureOnline();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql");
        var key = Key();
        if (key.Length > 0) request.Headers.Add("apikey", key);
        AddApplicationHeaders(request);
        request.Content = new StringContent(JsonConvert.SerializeObject(new { query, variables }), Encoding.UTF8, "application/json");
        var response = await SendApiRequest(request, ct);
        // GraphQL can return HTTP 200 with semantic errors. Never treat partial data as a complete result.
        if (response["errors"] is JArray errors && errors.Count > 0)
            throw new InvalidOperationException("N网公开目录接口暂不可用，请稍后重试或在浏览器打开原站。");
        if (response["data"] is not JObject data) throw new InvalidDataException("N网公开目录响应格式不正确。");
        return data;
    }

    private static void AddApplicationHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("Application-Name", "StarSprout");
        request.Headers.Add("Application-Version", "0.4.3");
        request.Headers.UserAgent.ParseAdd("StarSprout/0.4.3");
    }

    private async Task<JToken> SendApiRequest(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new InvalidOperationException("N网拒绝了访问，请检查 API Key、账号权限和下载授权。");
        if (response.StatusCode == (HttpStatusCode)429) throw new InvalidOperationException("N网请求次数达到限制，请稍后重试。");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"N网请求失败（{(int)response.StatusCode}）。");
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("N网响应过大。");
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { memory.Write(buffer, 0, count); if (memory.Length > 4 * 1024 * 1024) throw new InvalidDataException("N网响应过大。"); }
        return JToken.Parse(Encoding.UTF8.GetString(memory.ToArray()));
    }

    private string Key()
    {
        lock (sync)
        {
            if (stored.ProtectedApiKey.Length == 0) return "";
            var clear = LogAnalysisService.WindowsDpapi.Transform(Convert.FromBase64String(stored.ProtectedApiKey), false);
            try { return Encoding.UTF8.GetString(clear); } finally { Array.Clear(clear); }
        }
    }
    private long GetUserId() { lock (sync) return stored.UserId; }
    private void EnsureOnline() { if (content.GetState().Offline) throw new InvalidOperationException("当前为离线模式，N网登录和在线下载已暂停。"); }
    private static void Positive(long value) { if (value <= 0) throw new ArgumentException("Mod ID / File ID 必须是正整数。"); }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonConvert.SerializeObject(stored, Formatting.Indented)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class Stored
    {
        public string ProtectedApiKey { get; set; } = "";
        public string Username { get; set; } = "";
        public long UserId { get; set; }
        public bool IsPremium { get; set; }
        public string ApplicationId { get; set; } = "";
    }
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
