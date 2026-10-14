using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using StarSprout.Services;

var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; }
async Task Reject(Func<Task> action, string label)
{
    try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException) { checks++; return; }
    throw new Exception("FAIL: expected rejection " + label);
}
try
{
    var handler = new Handler();
    using var http = new HttpClient(handler);
    var stardew = new StardewService(Path.Combine(root, "game-settings"), Path.Combine(root, "saves"));
    using var content = new ContentService(Path.Combine(root, "content"), stardew, http);
    using var nexus = new NexusService(Path.Combine(root, "nexus"), content, http);
    Check(!nexus.GetState().Connected && handler.Calls == 0, "starts without network or credentials");
    Check(NexusService.NormalizeModPageUrl(" 2400 ").AbsoluteUri == "https://www.nexusmods.com/stardewvalley/mods/2400?tab=files", "pasted numeric ID opens built-in files page");
    Check(NexusService.NormalizeModPageUrl("https://nexusmods.com/games/stardewvalley/mods/2400?tab=files&file_id=456&nmm=1&key=secret#notes").AbsoluteUri ==
        "https://www.nexusmods.com/stardewvalley/mods/2400?tab=files&file_id=456&nmm=1", "modern file link retains public selectors and discards credentials and fragment");
    Check(NexusService.NormalizeModPageUrl("https://www.nexusmods.com/stardewvalley/mods/2400?tab=description").Query == "?tab=description", "description link stays on details");
    foreach (var input in new[] { "https://www.nexusmods.com/skyrim/mods/2400", "https://www.nexusmods.com.attacker.example/stardewvalley/mods/2400", "https://name:password@www.nexusmods.com/stardewvalley/mods/2400", "http://www.nexusmods.com/stardewvalley/mods/2400", "https://www.nexusmods.com:8443/stardewvalley/mods/2400", "https://www.nexusmods.com/stardewvalley/mods/2400?file_id=-1", "https://www.nexusmods.com/stardewvalley/mods/2400?file_id=1&file_id=2", "https://www.nexusmods.com/stardewvalley/mods/2400?file_id=1&nmm=3", "https://www.nexusmods.com/stardewvalley/mods/2400?tab=<script>", "9999999999999999999999999999999", "0" })
        await Reject(() => Task.FromResult(NexusService.NormalizeModPageUrl(input)), "invalid pasted link rejected");
    try
    {
        await nexus.HandleNxmAsync("nxm://stardewvalley/mods/123/files/45?key=site-granted&expires=4102444800&user_id=7");
        throw new Exception("FAIL: expected in-browser manual fallback without API credential");
    }
    catch (NexusBrowserDownloadRequiredException ex)
    {
        Check(ex.PageUrl.AbsoluteUri == "https://www.nexusmods.com/stardewvalley/mods/123?tab=files&file_id=45&nmm=0" && handler.Calls == 0,
            "website-only login falls back to original file inside browser with no API request");
        Check(!ex.Message.Contains("site-granted") && !ex.PageUrl.AbsoluteUri.Contains("site-granted"), "temporary NXM grant is not exposed by fallback");
    }
    var publicMod = await nexus.GetModAsync(123);
    Check(publicMod.Name == "Public Mod" && publicMod.Description == "Public description" && publicMod.Files.Count == 4, "anonymous official API returns complete details and files");
    Check(!handler.LastHasKey && publicMod.Files.All(f => !f.CanDownload), "public browsing needs no key and grants no direct download entitlement");
    Check(publicMod.Files[0].CategoryName == "MAIN" && publicMod.Files[0].IsZip, "current main file is listed before old files");
    Check(!publicMod.Files.Single(f => f.FileId == 48).IsZip && !publicMod.Files.Single(f => f.FileId == 49).Available, "unsupported archive and archived status remain distinguishable");
    var firstCatalog = await nexus.GetCatalogAsync();
    Check(firstCatalog.Items.Count == 24 && firstCatalog.Total == 30 && firstCatalog.HasMore && firstCatalog.Page == 1, "anonymous latest catalog reports real total and paging");
    Check(handler.LastGraphVariables?["sort"]?[0]?["createdAt"]?["direction"]?.ToString() == "DESC", "latest-added requests official creation sort");
    var secondCatalog = await nexus.GetCatalogAsync("latest_updated", null, 2);
    Check(secondCatalog.Items.Count == 6 && !secondCatalog.HasMore && secondCatalog.Items[0].ModId == 25, "second catalog page uses server offset and ends correctly");
    Check(handler.LastGraphVariables?.Value<int>("offset") == 24 && handler.LastGraphVariables?["sort"]?[0]?["updatedAt"] is not null, "updated list uses server sort and offset");
    await nexus.GetCatalogAsync("popular");
    Check(handler.LastGraphVariables?["sort"]?[0]?["downloads"] is not null, "popular list sorts by downloads without pretending to be trending");
    const string searchTerm = "Content \" Patcher";
    var searched = await nexus.GetCatalogAsync("latest_added", searchTerm);
    Check(searched.Query == searchTerm && searched.Total == 30 && handler.LastGraphVariables?["filter"]?["nameStemmed"]?["value"]?.ToString() == searchTerm,
        "full-site name search sends input safely as GraphQL variables");
    Check(handler.LastGraphVariables?["filter"]?["nameStemmed"]?["op"]?.ToString() == "MATCHES" && handler.LastGraphVariables?["sort"]?[0]?["relevance"] is not null,
        "name search uses verified token matching and relevance");
    Check(handler.LastGraphVariables?["filter"]?["gameDomainName"]?["value"]?.ToString() == "stardewvalley", "search restricted to Stardew Valley");
    var callsBeforeInvalid = handler.Calls;
    await Reject(() => nexus.GetCatalogAsync("arbitrary-endpoint"), "catalog category whitelist");
    await Reject(() => nexus.GetCatalogAsync(page: 0), "invalid page rejected");
    await Reject(() => nexus.GetCatalogAsync(query: new string('a', 121)), "oversized search rejected");
    await Reject(() => nexus.GetCatalogAsync(query: "a\nb"), "control characters rejected");
    Check(handler.Calls == callsBeforeInvalid, "invalid catalog input never makes network request");
    handler.GraphError = true;
    await Reject(() => nexus.GetCatalogAsync(), "HTTP 200 GraphQL errors are not accepted as empty results");
    handler.GraphError = false;
    handler.WrongGame = true;
    await Reject(() => nexus.GetCatalogAsync(), "wrong-game catalog response rejected");
    await Reject(() => nexus.GetModAsync(123), "wrong-game detail response rejected");
    handler.WrongGame = false;
    handler.NextStatus = HttpStatusCode.TooManyRequests;
    await Reject(() => nexus.GetCatalogAsync(), "API quota failure explained instead of treated as empty list");
    await Reject(() => nexus.StartSsoAsync(), "SSO requires registered application ID");
    nexus.SaveApplicationId("registered-test-app");
    Check(nexus.GetState().ApplicationId == "registered-test-app", "own application ID persists");
    const string key = "a-private-fixture-key-not-real-1234567890";
    var state = await nexus.SaveApiKeyAsync(key);
    Check(state.Connected && state.Username == "Fixture" && !state.IsPremium, "validate free account");
    Check(!File.ReadAllText(Path.Combine(root, "nexus", "nexus-settings.json")).Contains(key), "key encrypted on disk");
    Check(!Newtonsoft.Json.JsonConvert.SerializeObject(state).Contains(key), "state never discloses key");
    Check(handler.LastKey == key, "key sent only as API header");
    var trending = await nexus.GetCatalogAsync("trending");
    Check(trending.Items.Count == 1 && !trending.HasMore && handler.LastPath.EndsWith("trending.json"), "official trending fixed list is separate from paged popularity sort");
    var mod = await nexus.GetModAsync(123);
    Check(mod.Name == "Fixture Mod" && mod.Files.Single().FileId == 45, "fetch mod and files");
    Check(!mod.Files.Single().CanDownload, "free account direct link disabled");
    await Reject(() => nexus.DownloadFileAsync(123, 45, false), "free API direct downloads require entitlement");
    var nxm = NexusService.ParseNxm("nxm://stardewvalley/mods/123/files/45?key=abc%2Bdef&expires=4102444800&user_id=7");
    Check(nxm.ModId == 123 && nxm.FileId == 45 && nxm.Key == "abc+def", "valid NXM parsing");
    await Reject(() => nexus.HandleNxmAsync("nxm://stardewvalley/mods/" + new string('9', 80) + "/files/45"), "oversized numeric ID rejected without overflowing listener");
    await Reject(() => nexus.HandleNxmAsync("nxm://skyrim/mods/123/files/45"), "reject wrong game");
    await Reject(() => nexus.HandleNxmAsync("nxm://stardewvalley/mods/123/files/45?key=x&expires=1&user_id=7"), "reject expired authorization");
    await Reject(() => nexus.HandleNxmAsync("nxm://stardewvalley/mods/123/files/45?key=x&expires=4102444800&user_id=99"), "reject other account authorization");
    await Reject(() => nexus.HandleNxmAsync("nxm://stardewvalley/mods/123/files/45"), "free link must include authorization");
    var downloaded = await nexus.HandleNxmAsync("nxm://stardewvalley/mods/123/files/45?key=abc&expires=4102444800&user_id=7");
    Check(File.Exists(downloaded.Path) && !downloaded.Installed, "authorized NXM download caches ZIP without game changes");
    Check(content.GetState().Downloads.Single().SourceUrl == "https://www.nexusmods.com/stardewvalley/mods/123", "cached Nexus file retains public mod source instead of signed CDN URL");
    handler.Premium = true;
    await nexus.SaveApiKeyAsync(key);
    Check(nexus.GetState().IsPremium, "premium account validated");
    downloaded = await nexus.DownloadFileAsync(123, 45, false);
    Check(downloaded.Bytes > 0, "premium API ZIP download");
    Check(handler.KeysReachedOnlyOfficialApi, "API credentials never reach public file CDN");
    content.SetOffline(true);
    var callsBeforeOffline = handler.Calls;
    await Reject(() => nexus.GetModAsync(123), "offline mode forbids network");
    await Reject(() => nexus.GetCatalogAsync(), "offline catalog forbids network");
    Check(handler.Calls == callsBeforeOffline, "offline requests do not contact API");
    nexus.ClearKey();
    Check(!nexus.GetState().HasApiKey && !nexus.GetState().Connected, "disconnect clears key");
    if (args.Contains("--live"))
    {
        using var liveContent = new ContentService(Path.Combine(root, "live-content"), stardew);
        using var liveNexus = new NexusService(Path.Combine(root, "live-nexus"), liveContent);
        var liveCatalog = await liveNexus.GetCatalogAsync("latest_added");
        Check(liveCatalog.Items.Count > 0 && liveCatalog.Total > 1000 && liveCatalog.Items.All(m => m.SourceUrl.Contains("/stardewvalley/")), "live anonymous official catalog");
        var liveSearch = await liveNexus.GetCatalogAsync("popular", "Content Patcher");
        Check(liveSearch.Total > 0 && liveSearch.Items.Count > 0, "live official full-site name search");
        var liveDetail = await liveNexus.GetModAsync(1915);
        Check(liveDetail.Name == "Content Patcher" && liveDetail.Description.Length > 0 && liveDetail.Files.Any(f => f.IsZip && f.Available), "live anonymous public details and file list");
        Check(!liveNexus.GetState().HasApiKey && liveDetail.Files.All(f => !f.CanDownload), "live public browsing never grants premium download entitlement");
        Console.WriteLine($"LIVE: official anonymous catalog {liveCatalog.Total} mods, name search {liveSearch.Total} hits, Content Patcher {liveDetail.Files.Count} files; no account or download.");
    }
    Console.WriteLine($"PASS: {checks} Nexus checks; {(args.Contains("--live") ? "mocked API plus live anonymous metadata" : "mocked API only")}, no login or real download performed.");
}
finally
{
    var allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture path");
    Directory.Delete(root, true);
}

sealed class Handler : HttpMessageHandler
{
    public bool Premium;
    public int Calls;
    public string LastKey = "";
    public bool LastHasKey;
    public bool KeysReachedOnlyOfficialApi = true;
    public JObject? LastGraphVariables;
    public string LastPath = "";
    public bool GraphError;
    public bool WrongGame;
    public HttpStatusCode? NextStatus;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        if (request.RequestUri!.Host != "api.nexusmods.com")
        {
            KeysReachedOnlyOfficialApi &= !request.Headers.Contains("apikey");
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("Fixture/manifest.json").Open()))
                writer.Write("{\"Name\":\"Fixture\",\"Author\":\"Fixture\",\"Version\":\"1.0.0\",\"UniqueID\":\"Fixture.Nexus\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
        }
        LastHasKey = request.Headers.TryGetValues("apikey", out var keys);
        LastKey = keys?.Single() ?? "";
        var path = request.RequestUri.AbsolutePath;
        LastPath = path;
        if (NextStatus is HttpStatusCode status) { NextStatus = null; return new(status) { Content = new StringContent("{}") }; }
        if (path == "/v2/graphql")
        {
            var input = JObject.Parse(await request.Content!.ReadAsStringAsync(ct));
            LastGraphVariables = (JObject?)input["variables"];
            if (GraphError) return Json("{\"errors\":[{\"message\":\"Schema changed\"}],\"data\":{\"mods\":null}}");
            int gameId = WrongGame ? 1704 : 1303;
            if (input.Value<string>("query")!.Contains("StarSproutMod"))
                return Json(new JObject { ["data"] = new JObject
                {
                    ["mods"] = new JObject { ["nodes"] = new JArray(new JObject { ["modId"] = 123, ["gameId"] = gameId, ["name"] = "Public Mod", ["description"] = "Public description" }) },
                    ["modFiles"] = new JArray(
                        new JObject { ["fileId"] = 46, ["name"] = "Old ZIP", ["version"] = "0.9", ["category"] = "OLD_VERSION", ["uri"] = "old.zip" },
                        new JObject { ["fileId"] = 45, ["name"] = "Current ZIP", ["version"] = "1.0", ["category"] = "MAIN", ["uri"] = "current.zip", ["sizeInBytes"] = "512" },
                        new JObject { ["fileId"] = 48, ["name"] = "RAR", ["category"] = "OPTIONAL", ["uri"] = "other.rar" },
                        new JObject { ["fileId"] = 49, ["name"] = "Archived", ["category"] = "ARCHIVED", ["uri"] = "archived.zip" })
                } }.ToString());
            int offset = LastGraphVariables?.Value<int>("offset") ?? 0;
            var nodes = new JArray(Enumerable.Range(offset + 1, Math.Max(0, Math.Min(24, 30 - offset))).Select(i => new JObject { ["modId"] = i, ["gameId"] = gameId, ["name"] = "Fixture " + i, ["pictureUrl"] = "https://attacker.example/image.png" }));
            return Json(new JObject { ["data"] = new JObject { ["mods"] = new JObject { ["totalCount"] = 30, ["nodes"] = nodes } } }.ToString());
        }
        string body = path.Contains("validate") ? $"{{\"user_id\":7,\"name\":\"Fixture\",\"is_premium\":{Premium.ToString().ToLowerInvariant()}}}" :
            path.Contains("download_link") ? "[{\"URI\":\"https://files.example.com/fixture.zip\"}]" :
            path.EndsWith("trending.json") ? "[{\"mod_id\":123,\"game_id\":1303,\"name\":\"Trending Fixture\"}]" :
            path.EndsWith("files.json") ? "{\"files\":[{\"file_id\":45,\"name\":\"Fixture ZIP\",\"file_name\":\"fixture.zip\",\"version\":\"1.0\",\"size_in_bytes\":512}]}" :
            "{\"name\":\"Fixture Mod\",\"author\":\"Fixture\",\"summary\":\"Test only\"}";
        return Json(body);
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}
