using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StarSprout.Services;

var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); ++count; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name) { try { await action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or HttpRequestException) { Check(true, name); return; } throw new Exception(name + " did not reject"); }

if (args.Contains("--live"))
{
    using var live = new CommunityCatalogService();
    var mods = await live.ListAsync("3dm", "Daily Screenshot", 1);
    Check(mods.Items.Count > 0 && mods.Items.All(x => x.Source == "3dm"), "Live 3DM anonymous search");
    var liveDetail = await live.GetDetailAsync("3dm", "257914");
    Check(liveDetail.Downloads.Any(d => d.CanInstall && d.Url.StartsWith("https://dmod.3dmgame.com/")), "Live 3DM public ZIP metadata");
    var livePosts = await live.ListAsync("smallfire", "", 1);
    Check(livePosts.Items.Count > 0, "Live smallfire anonymous list");
    var livePost = await live.GetDetailAsync("smallfire", "114");
    Check(livePost.Description.Contains("CJB") && livePost.Downloads.All(d => !d.CanInstall), "Live smallfire Nexus permissions preserved");
    Console.WriteLine($"Live community checks passed: {count}");
    return;
}

const string zip = "https://dmod.3dmgame.com/static/upload/resource2/202608/example.zip";
const string list = """
{"success":true,"data":{"count":50,"mods":[{"id":257914,"game_id":10,"mods_title":"<b>Daily Screenshot</b>","mods_desc":"截图工具","mods_image_url":"/static/upload/test.png","user_nickName":"publisher"},{"id":999,"game_id":265,"mods_title":"Wrong game"},{"id":12,"game_id":10,"mods_title":"Other","mods_image_url":"https://evil.test/image.jpg"}]}}
""";
var detailJson = JsonConvert.SerializeObject(new
{
    success = true, data = new
    {
        id = 257914, game_id = 10, mods_title = "Daily Screenshot", mods_author = "Creator", mods_version = "3.4.1",
        mods_desc = "截图工具", mods_content = "<h2>用途</h2><script>bad()</script><p>正常 &amp; 安全</p>",
        mods_resource = new[]
        {
            new { mods_resource_name = "Mod.zip", mods_resource_url = zip },
            new { mods_resource_name = "Cloud", mods_resource_url = "https://pan.xunlei.com/s/example" },
            new { mods_resource_name = "RAR", mods_resource_url = "https://dmod.3dmgame.com/static/upload/example.rar" },
            new { mods_resource_name = "Unsafe", mods_resource_url = "http://dmod.3dmgame.com/static/upload/example.zip" },
            new { mods_resource_name = "Credentials", mods_resource_url = "https://secret:pwd@dmod.3dmgame.com/static/upload/example.zip" },
            new { mods_resource_name = "Not attachment", mods_resource_url = "https://dmod.3dmgame.com/mod/view.zip" },
            new { mods_resource_name = "Loopback", mods_resource_url = "https://127.0.0.1/example.zip" },
            new { mods_resource_name = "Spoof", mods_resource_url = "https://dmod.3dmgame.com.evil.test/static/upload/example.zip" }
        }
    }
});
const string smallList = """
{"links":{"next":"https://svmbbs.smallfire.cn/api/discussions?page=2"},"data":[{"id":"114","attributes":{"title":"CJB 汉化"},"relationships":{"user":{"data":{"id":"165"}}}}],"included":[{"type":"users","id":"165","attributes":{"displayName":"lihang"}}]}
""";
var smallDetail = JsonConvert.SerializeObject(new
{
    data = new
    {
        id = "114", attributes = new { title = "CJB 汉化" },
        relationships = new { user = new { data = new { id = "165" } }, firstPost = new { data = new { id = "268" } } }
    },
    included = new object[]
    {
        new { type = "users", id = "165", attributes = new { displayName = "lihang" } },
        new { type = "posts", id = "268", attributes = new { number = 1, contentHtml = "<p>CJB 描述</p><a href=\"https://www.nexusmods.com/stardewvalley/mods/23460\">模组</a><a href=\"https://www.nexusmods.com/stardewvalley/mods/23460\">重复</a><script>secret()</script>" } }
    }
});

using var handler = new FakeHandler();
using var client = new HttpClient(handler);
using var service = new CommunityCatalogService("unused-community-data", client);
Check(service.GetState().Sources.Count == 3, "Three community sources exposed");
Check(!service.GetState().Sources.Single(x => x.Id == "esous").SupportsApi, "Unverified ESOUS API not fabricated");
handler.Reply = list;
var result = await service.ListAsync("3dm", "截图", 1);
Check(result.Items.Count == 2 && result.Items.All(x => x.Source == "3dm"), "Non-Stardew resources excluded");
Check(result.Items[0].Name == "Daily Screenshot", "Metadata stripped to plain text");
Check(result.Items[0].ThumbnailUrl == "https://mod.3dmgame.com/static/upload/test.png", "Safe relative thumbnail resolved");
Check(result.Items[1].ThumbnailUrl.Length == 0, "External thumbnail excluded");
Check(result.HasMore && result.Page == 1, "Pagination count preserved");
var request = JObject.Parse(handler.Body);
Check(request["gameId"] is JArray a && a.Count == 1 && a[0].Value<int>() == 10, "Actual Stardew game ID array used");
Check(request.Value<int>("pageSize") == 24 && request.Value<string>("search") == "截图", "Search and bounded page size sent");
Check(handler.Cookie.Length == 0 && handler.Authorization.Length == 0, "Anonymous requests contain no credentials");

handler.Reply = detailJson;
var detail = await service.GetDetailAsync("3dm", "257914");
Check(detail.Author == "Creator" && detail.Version == "3.4.1", "Author and version preserved");
Check(detail.Description.Contains("正常 & 安全") && !detail.Description.Contains("bad()") && !detail.Description.Contains("<"), "Remote HTML cannot execute");
Check(detail.DownloadUrl == zip, "Verified public ZIP selected");
Check(detail.Downloads.Count(d => d.CanInstall) == 1, "Only verified 3DM public attachment path installs");
Check(detail.Downloads.Any(d => d.Url.Contains("xunlei") && !d.CanInstall), "Cloud download kept as browse link");
Check(!detail.Downloads.Any(d => d.Url.StartsWith("http:") || d.Url.Contains("secret:") || d.Url.Contains("127.0.0.1")), "Unsafe download URLs excluded");
Check(detail.Warnings.Count > 0, "Non-ZIP download limitations visible");
var payload = JObject.Parse(JsonConvert.SerializeObject(detail));
Check(payload["downloads"]?[0]?["name"] is not null && payload["downloads"]?[0]?["url"] is not null && payload["downloads"]?[0]?["canInstall"] is not null, "Download contract is name/url/canInstall");
handler.Reply = "{\"success\":true,\"data\":{\"id\":257914,\"game_id\":265}}";
await Reject(() => service.GetDetailAsync("3dm", "257914"), "Wrong-game detail rejected");

handler.Reply = smallList;
var posts = await service.ListAsync("smallfire", "汉化 & CJB", 2);
Check(posts.Items[0].Author == "lihang" && posts.HasMore, "Flarum included author and next page parsed");
Check(handler.LastUrl.Contains("page%5Boffset%5D=24") && handler.LastUrl.Contains("%26"), "Search safely encoded without query injection");
handler.Reply = smallDetail;
var post = await service.GetDetailAsync("smallfire", "114");
Check(post.Description.Contains("CJB 描述") && !post.Description.Contains("secret()"), "Flarum first-post details parsed safely");
Check(post.Downloads.Count == 1 && !post.Downloads[0].CanInstall && post.DownloadUrl == "", "Nexus page not masqueraded as ZIP");
Check(post.SourceUrl == "https://svmbbs.smallfire.cn/d/114", "Stable first-party source link generated");

handler.Reply = "{\"success\":false,\"msg\":\"private server detail\"}";
await Reject(() => service.ListAsync("3dm"), "Community API failure rejected");
handler.Reply = "<html>login</html>";
await Reject(() => service.ListAsync("3dm"), "HTML response rejected");
handler.Status = HttpStatusCode.Found;
await Reject(() => service.ListAsync("3dm"), "API redirect rejected");
handler.Status = HttpStatusCode.Forbidden;
await Reject(() => service.ListAsync("3dm"), "Access restriction respected");
handler.Status = HttpStatusCode.OK;
handler.Reply = new string('x', 4 * 1024 * 1024 + 1);
await Reject(() => service.ListAsync("3dm"), "Oversized response rejected");
var previous = handler.Calls;
await Reject(() => service.GetDetailAsync("3dm", "1/../../private"), "Path-like ID rejected before network");
await Reject(() => service.ListAsync("3dm", page: 0), "Invalid pagination rejected before network");
await Reject(() => service.ListAsync("3dm", new string('a', 121)), "Oversized query rejected before network");
await Reject(() => service.ListAsync("esous"), "Unverified source API rejected before network");
service.Offline = true;
await Reject(() => service.ListAsync("3dm"), "Offline search blocked before network");
Check(handler.Calls == previous, "Invalid inputs and offline actions send zero requests");
Check(service.GetState().Offline, "Offline mode appears in state");
Console.WriteLine($"Community checks passed: {count}");

sealed class FakeHandler : HttpMessageHandler
{
    public string Reply { get; set; } = "{}";
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Body { get; private set; } = "";
    public string LastUrl { get; private set; } = "";
    public string Cookie { get; private set; } = "";
    public string Authorization { get; private set; } = "";
    public int Calls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ++Calls;
        LastUrl = request.RequestUri!.AbsoluteUri;
        Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : "";
        Authorization = request.Headers.Authorization?.ToString() ?? "";
        return new HttpResponseMessage(Status) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
    }
}
