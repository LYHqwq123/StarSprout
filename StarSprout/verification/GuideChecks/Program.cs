using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using StarSprout.Services;

string directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or HttpRequestException or OperationCanceledException) { checks++; return; }
    throw new Exception("FAIL: expected rejection: " + message);
}

using var handler = new FakeWiki();
using var http = new HttpClient(handler);
using var guide = new GuideService(directory, http);
Check(guide.GetState().CacheCount == 0 && handler.Calls == 0, "starting reader never connects automatically");
var featured = await guide.SearchAsync("");
Check(featured.Items.Count == 12 && featured.Items[0].Title == "农场" && handler.Calls == 0, "curated entry points work without network");
Check(GuideService.NormalizeTitle("https://wiki.biligame.com/stardewvalley/%E5%86%9C%E5%9C%BA#test") == "农场", "known wiki URL normalizes to article title");
foreach (string invalid in new[] { "", "https://wiki.biligame.com.attacker.example/stardewvalley/农场", "http://wiki.biligame.com/stardewvalley/农场", "https://name:secret@wiki.biligame.com/stardewvalley/农场", "https://wiki.biligame.com:8443/stardewvalley/农场", "https://wiki.biligame.com/other/农场", "https://wiki.biligame.com/stardewvalley/农场?action=edit", "api.php", "a\nb", "<script>" })
    await Reject(() => guide.GetArticleAsync(invalid), "untrusted URL/title");
await Reject(() => guide.SearchAsync(new string('a', 121)), "search length");
await Reject(() => guide.SearchAsync("a\nb"), "search control character");
await Reject(() => guide.SearchAsync("农场", 0), "page lower bound");
await Reject(() => guide.SearchAsync("农场", 101), "page upper bound");
Check(handler.Calls == 0, "invalid inputs never reach network");
var search = await guide.SearchAsync("钓鱼");
Check(search.Items.Count == 2 && search.Items[0].Summary == "钓鱼指南" && search.Total == 30 && search.HasMore, "official search results preserve page metadata and plain snippets");
Check(handler.LastUri!.Query.Contains("srnamespace=0") && handler.LastUri.Query.Contains("sroffset=0"), "search stays in article namespace");
var second = await guide.SearchAsync("钓鱼", 2);
Check(second.Page == 2 && !second.HasMore && handler.LastUri.Query.Contains("sroffset=20"), "search pagination follows API continuation");
int before = handler.Calls;
var cachedSearch = await guide.SearchAsync("钓鱼");
Check(cachedSearch.FromCache && handler.Calls == before, "fresh search cache avoids repeated API requests");
var article = await guide.GetArticleAsync("农场");
Check(article.Title == "农场" && article.LastUpdated == "2026-10-01T04:00:00Z" && article.CachedAt.Length > 0, "revision update time remains distinct from cache time");
Check(article.Sections.Count == 1 && article.Sections[0].Title == "地图" && article.Sections[0].Anchor == "地图", "article section metadata is plain text");
Check(article.Html.Contains("<table>") && article.Html.Contains("colspan=\"2\"") && article.Html.Contains("id=\"地图\""), "article tables and heading anchors are preserved");
Check(article.Html.Contains("https://patchwiki.biligame.com/images/stardewvalley/test.png") && article.Html.Contains("loading=\"lazy\""), "public image URL normalizes to HTTPS and loads lazily");
Check(!article.Html.Contains("<script", StringComparison.OrdinalIgnoreCase) && !article.Html.Contains("onclick", StringComparison.OrdinalIgnoreCase) && !article.Html.Contains("onerror", StringComparison.OrdinalIgnoreCase) && !article.Html.Contains("<iframe", StringComparison.OrdinalIgnoreCase) && !article.Html.Contains("<style", StringComparison.OrdinalIgnoreCase), "active content and event attributes removed");
Check(!article.Html.Contains("attacker.example") && !article.Html.Contains("javascript:") && !article.Html.Contains("data:"), "remote embeds and script/data links cannot survive sanitization");
Check(article.Html.Contains("https://wiki.biligame.com/stardewvalley/%E9%92%93%E9%B1%BC"), "internal encyclopedia links remain readable links");
Check(article.SourceUrl.EndsWith("%E5%86%9C%E5%9C%BA") && article.License.Contains("CC BY-NC-SA 3.0"), "source provenance and content license accompany article");
Check(guide.GetState().Recent.Single().Title == "农场" && guide.GetState().CacheCount == 1, "successful read records bounded local history and body cache");
Check(guide.SetFavorite("农场", true).Favorites.Count == 1 && guide.SetFavorite("农场", true).Favorites.Count == 1, "favorite toggle deduplicates");
using (var reopened = new GuideService(directory, http))
    Check(reopened.GetState().Favorites.Single().Title == "农场", "favorites persist between launcher runs");
Check(guide.SetFavorite("农场", false).Favorites.Count == 0, "unfavorite removes entry");
// A redirect's canonical title is what the reader favorites; both names must share the offline body.
using (var aliases = new GuideService(Path.Combine(directory, "aliases"), http))
{
    var redirected = await aliases.GetArticleAsync("农场别名");
    aliases.SetFavorite(redirected.Title, true);
    aliases.Offline = true;
    Check((await aliases.GetArticleAsync(redirected.Title)).FromCache, "canonical favorite opens offline after initial alias read");
    Check((await aliases.GetArticleAsync("农场别名")).FromCache && aliases.GetState().CacheCount == 1, "alias and canonical title share one offline cache entry");
    using var reopenedAliases = new GuideService(Path.Combine(directory, "aliases"), http) { Offline = true };
    Check((await reopenedAliases.GetArticleAsync("农场别名")).FromCache && (await reopenedAliases.GetArticleAsync("农场")).FromCache, "alias map and canonical favorite survive launcher restart");
}
before = handler.Calls;
guide.Offline = true;
var offlineArticle = await guide.GetArticleAsync("农场");
Check(offlineArticle.FromCache && offlineArticle.Notice.Contains("图片") && handler.Calls == before, "offline article uses body cache without API request and explains image availability");
var offlineSearch = await guide.SearchAsync("钓鱼");
Check(offlineSearch.FromCache && handler.Calls == before, "offline cached search needs no API");
await Reject(() => guide.GetArticleAsync("未缓存条目"), "uncached offline article");
await Reject(() => guide.SearchAsync("未缓存搜索"), "uncached offline search");
Check(handler.Calls == before, "uncached offline requests never contact API");
guide.Offline = false;
handler.ReturnInvalidJson = true;
await Reject(() => guide.SearchAsync("bad-json"), "invalid JSON is not empty success");
handler.ReturnInvalidJson = false;
handler.ReturnApiError = true;
await Reject(() => guide.GetArticleAsync("不存在的条目"), "HTTP 200 API error rejected");
handler.ReturnApiError = false;
handler.Status = HttpStatusCode.Forbidden;
await Reject(() => guide.SearchAsync("forbidden"), "access restrictions surfaced without workaround");
handler.Status = HttpStatusCode.Redirect;
await Reject(() => guide.SearchAsync("redirect"), "redirect response not followed");
handler.Status = HttpStatusCode.OK;
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); before = handler.Calls;
    await Reject(() => guide.GetArticleAsync("农场", cancelled.Token), "cancellation must not silently show cache");
    Check(handler.Calls == before, "already cancelled request does not connect");
}
Check(GuideService.SafeImageUrl("//patchwiki.biligame.com/images/stardewvalley/test.png").StartsWith("https://"), "known protocol-relative image upgraded");
foreach (string invalid in new[] { "http://patchwiki.biligame.com/images/stardewvalley/a.png", "https://patchwiki.biligame.com.attacker.example/images/stardewvalley/a.png", "https://patchwiki.biligame.com/images/anothergame/a.png", "https://patchwiki.biligame.com/images/stardewvalley/a.png?redirect=1", "data:image/svg+xml,<svg onload=alert(1)>", "https://u:p@patchwiki.biligame.com/images/stardewvalley/a.png" })
    Check(GuideService.SafeImageUrl(invalid) == "", "untrusted image rejected");
string tricky = GuideService.SanitizeHtml("<p>&lt;script&gt;safe&lt;/script&gt;</p><img src=\"https://patchwiki.biligame.com/images/stardewvalley/a.png\" onerror='bad'><svg><foreignObject><p>bad</p></foreignObject></svg><p>after</p>");
Check(tricky.Contains("&lt;script&gt;safe&lt;/script&gt;") && tricky.Contains("after") && !tricky.Contains("<svg") && !tricky.Contains("bad"), "escaped text and foreign content cannot reactivate in reader");
// Expire caches and make network unavailable: last selected pages still remain usable.
foreach (string path in Directory.EnumerateFiles(directory, "*-*.json"))
{
    if (Path.GetFileName(path) == "reading-state.json") continue;
    var content = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
    content["CachedAt"] = "2000-01-01T00:00:00Z";
    File.WriteAllText(path, content.ToString());
}
handler.Status = HttpStatusCode.ServiceUnavailable;
Check((await guide.GetArticleAsync("农场")).Notice.Contains("网络") && (await guide.SearchAsync("钓鱼")).Notice.Contains("网络"), "expired caches recover from network outage");

if (args.Contains("--live"))
{
    using var live = new GuideService(Path.Combine(directory, "live"));
    var liveSearch = await live.SearchAsync("钓鱼");
    Check(liveSearch.Items.Count > 0 && liveSearch.Items.Any(item => item.Title.Contains("钓鱼")), "live anonymous BWIKI search");
    var liveArticle = await live.GetArticleAsync("农场");
    Check(liveArticle.Html.Length > 1000 && liveArticle.Sections.Count > 0 && liveArticle.LastUpdated.Length > 0, "live anonymous farm article and revision metadata");
    var image = Regex.Match(liveArticle.Html, "<img[^>]+src=\"([^\"]+)\"").Groups[1].Value;
    Check(GuideService.SafeImageUrl(image).Length > 0, "live article retains safe public image");
    using var imageHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    using var imageResponse = await imageHttp.GetAsync(WebUtility.HtmlDecode(image), HttpCompletionOption.ResponseHeadersRead);
    Check(imageResponse.IsSuccessStatusCode && imageResponse.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true, "live public image works without cookies or Referer bypass");
    Console.WriteLine($"Live BWIKI: {liveSearch.Items.Count} search results, {liveArticle.Html.Length} safe HTML characters, image HTTP {(int)imageResponse.StatusCode}.");
}
Console.WriteLine($"PASS: {checks} guide checks. Fixtures: {directory}");

sealed class FakeWiki : HttpMessageHandler
{
    public int Calls { get; private set; }
    public Uri? LastUri { get; private set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public bool ReturnInvalidJson { get; set; }
    public bool ReturnApiError { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++; LastUri = request.RequestUri;
        if (request.RequestUri!.GetLeftPart(UriPartial.Path) != "https://wiki.biligame.com/stardewvalley/api.php") throw new Exception("Unexpected endpoint");
        if (request.Headers.Authorization != null || request.Headers.Contains("Cookie")) throw new Exception("Anonymous wiki must not send credentials");
        string json;
        if (ReturnInvalidJson) json = "<html>unavailable</html>";
        else if (ReturnApiError) json = "{\"error\":{\"info\":\"The page does not exist\"}}";
        else if (request.RequestUri.Query.Contains("list=search"))
            json = JsonConvert.SerializeObject(new Dictionary<string, object> { ["query"] = new { searchinfo = new { totalhits = 30 }, search = new[] { new { title = "钓鱼", snippet = "<span class=\"searchmatch\">钓鱼</span>指南" }, new { title = "钓鱼竿", snippet = "工具" } } }, ["continue"] = request.RequestUri.Query.Contains("sroffset=20") ? new { } : (object)new { sroffset = 20 } });
        else if (request.RequestUri.Query.Contains("action=parse"))
            json = JsonConvert.SerializeObject(new { parse = new { title = "农场", text = "<div><h2 id=\"地图\">地图</h2><p onclick=\"bad\">正文</p><table><tr><td colspan=\"2\">耕地</td></tr></table><a href=\"/stardewvalley/钓鱼\">钓鱼</a><img src=\"//patchwiki.biligame.com/images/stardewvalley/test.png\" onerror=\"bad\"><script>bad()</script><style>@import 'https://attacker.example';</style><iframe src=\"https://attacker.example\"></iframe><img src=\"data:bad\"><a href=\"javascript:bad()\">badlink</a><a href=\"https://attacker.example\">external</a></div>", sections = new[] { new { line = "<b>地图</b>", anchor = "地图", level = "2" } } } });
        else json = "{\"query\":{\"pages\":[{\"revisions\":[{\"timestamp\":\"2026-10-01T04:00:00Z\"}]}]}}";
        var response = new HttpResponseMessage(Status) { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (Status == HttpStatusCode.Redirect) response.Headers.Location = new Uri("https://attacker.example/");
        return Task.FromResult(response);
    }
}
