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
    try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { checks++; return; }
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
    await Reject(() => nexus.GetModAsync(5), "authentication required");
    await Reject(() => nexus.StartSsoAsync(), "SSO requires registered application ID");
    nexus.SaveApplicationId("registered-test-app");
    Check(nexus.GetState().ApplicationId == "registered-test-app", "own application ID persists");
    const string key = "a-private-fixture-key-not-real-1234567890";
    var state = await nexus.SaveApiKeyAsync(key);
    Check(state.Connected && state.Username == "Fixture" && !state.IsPremium, "validate free account");
    Check(!File.ReadAllText(Path.Combine(root, "nexus", "nexus-settings.json")).Contains(key), "key encrypted on disk");
    Check(!Newtonsoft.Json.JsonConvert.SerializeObject(state).Contains(key), "state never discloses key");
    Check(handler.LastKey == key, "key sent only as API header");
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
    content.SetOffline(true);
    await Reject(() => nexus.GetModAsync(123), "offline mode forbids network");
    nexus.ClearKey();
    Check(!nexus.GetState().HasApiKey && !nexus.GetState().Connected, "disconnect clears key");
    Console.WriteLine($"PASS: {checks} Nexus checks; mocked API only, no login or real download performed.");
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
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        if (request.RequestUri!.Host != "api.nexusmods.com")
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("Fixture/manifest.json").Open()))
                writer.Write("{\"Name\":\"Fixture\",\"Author\":\"Fixture\",\"Version\":\"1.0.0\",\"UniqueID\":\"Fixture.Nexus\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
        }
        LastKey = request.Headers.GetValues("apikey").Single();
        var path = request.RequestUri.AbsolutePath;
        string body = path.Contains("validate") ? $"{{\"user_id\":7,\"name\":\"Fixture\",\"is_premium\":{Premium.ToString().ToLowerInvariant()}}}" :
            path.Contains("download_link") ? "[{\"URI\":\"https://files.example.com/fixture.zip\"}]" :
            path.EndsWith("files.json") ? "{\"files\":[{\"file_id\":45,\"name\":\"Fixture ZIP\",\"file_name\":\"fixture.zip\",\"version\":\"1.0\",\"size_in_bytes\":512}]}" :
            "{\"name\":\"Fixture Mod\",\"author\":\"Fixture\",\"summary\":\"Test only\"}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
