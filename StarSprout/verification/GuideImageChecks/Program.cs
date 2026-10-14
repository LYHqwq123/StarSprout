using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using StarSprout.Services;

string root = Path.Combine(AppContext.BaseDirectory, "image-check-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
var imageUrl = "https://patchwiki.biligame.com/images/stardewvalley/a/ab/example.png";
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6ElIAAAAASUVORK5CYII=");
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
async Task Fails(Func<Task> action, string name, string? message = null)
{
    try { await action(); }
    catch (Exception ex) when (message == null || ex.Message.Contains(message, StringComparison.Ordinal)) { passed++; return; }
    throw new Exception("FAIL: " + name);
}
string Folder(string name) => Path.Combine(root, name);
HttpResponseMessage Picture(byte[]? bytes = null, string contentType = "image/png")
{
    var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? png) };
    result.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
    return result;
}
HttpResponseMessage Redirect(string url)
{
    var result = new HttpResponseMessage(HttpStatusCode.Found);
    result.Headers.Location = new Uri(url, UriKind.RelativeOrAbsolute);
    return result;
}
try
{
    Check(GuideImageCache.IsAllowedUrl(imageUrl), "public BWIKI image allowed");
    Check(GuideImageCache.IsAllowedUrl(imageUrl.Replace("https://", "https://").Replace(".com/", ".com:443/")), "explicit HTTPS default port allowed");
    foreach (var url in new[]
    {
        "http://patchwiki.biligame.com/images/stardewvalley/a.png", "https://patchwiki.biligame.com:444/images/stardewvalley/a.png",
        "https://127.0.0.1/images/stardewvalley/a.png", "https://localhost/images/stardewvalley/a.png",
        "https://patchwiki.biligame.com.evil.test/images/stardewvalley/a.png", "https://u:p@patchwiki.biligame.com/images/stardewvalley/a.png",
        "https://patchwiki.biligame.com/images/othergame/a.png", "https://patchwiki.biligame.com/images/stardewvalley/",
        "https://patchwiki.biligame.com/images/stardewvalley/../../a.png", "https://patchwiki.biligame.com/images/stardewvalley/a.png#x",
        "https://patchwiki.biligame.com/images/stardewvalley/%252e%252e/a.png", "https://patchwiki.biligame.com/images/stardewvalley/a%zz.png",
        "https://patchwiki.biligame.com/images/stardewvalley/a\\b.png", " https://patchwiki.biligame.com/images/stardewvalley/a.png"
    }) Check(!GuideImageCache.IsAllowedUrl(url), "disallowed URL: " + url);

    var handler = new FakeHandler((_, _) => Picture());
    using (var client = new HttpClient(handler))
    using (var cache = new GuideImageCache(Folder("basic"), client))
    {
        var result = await cache.GetAsync(imageUrl);
        Check(result.ContentType == "image/png" && result.Bytes.SequenceEqual(png), "image signature and bytes retained");
        Check(handler.Requests == 1, "one lazy request");
        cache.Offline = true;
        var offline = await cache.GetAsync(imageUrl);
        Check(offline.Bytes.SequenceEqual(png) && handler.Requests == 1, "offline cached hit makes no request");
        await Fails(() => cache.GetAsync(imageUrl + "?other=1"), "offline missing image refused", "离线");
        Check(handler.Requests == 1, "offline miss makes no network request");
        await Fails(() => cache.GetAsync("https://localhost/x"), "unsafe URL refused before request");
        Check(handler.Requests == 1, "unsafe URL makes no network request");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Fails(() => cache.GetAsync(imageUrl, cancelled.Token), "cancelled request refused");
        Check(handler.Requests == 1, "cancelled request makes no network request");
    }
    using (var cache = new GuideImageCache(Folder("basic"), new HttpClient(new FakeHandler((_, _) => throw new Exception("No request expected")))))
    {
        cache.Offline = true;
        Check((await cache.GetAsync(imageUrl)).ContentType == "image/png", "cache persists between instances");
    }

    var redirectHandler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("example.png")
        ? Redirect("final.png") : Picture());
    using (var cache = new GuideImageCache(Folder("redirect"), new HttpClient(redirectHandler)))
    {
        Check((await cache.GetAsync(imageUrl)).Bytes.SequenceEqual(png) && redirectHandler.Requests == 2, "same site redirect works");
    }
    var evilRedirectHandler = new FakeHandler((_, _) => Redirect("https://127.0.0.1/private.png"));
    using (var cache = new GuideImageCache(Folder("evil-redirect"), new HttpClient(evilRedirectHandler)))
    {
        await Fails(() => cache.GetAsync(imageUrl), "private redirected URL blocked");
        Check(evilRedirectHandler.Requests == 1, "blocked redirect not requested");
    }
    var loop = new FakeHandler((_, _) => Redirect(imageUrl));
    using (var cache = new GuideImageCache(Folder("redirect-loop"), new HttpClient(loop)))
    {
        await Fails(() => cache.GetAsync(imageUrl), "redirect count bounded");
        Check(loop.Requests == 4, "at most three redirects followed");
    }
    foreach (var (name, bytes, type) in new[]
    {
        ("html-as-png", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), "image/png"),
        ("svg", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"), "image/svg+xml"),
        ("mime-mismatch", png, "image/jpeg"),
        ("empty", Array.Empty<byte>(), "image/png")
    })
    {
        using var cache = new GuideImageCache(Folder(name), new HttpClient(new FakeHandler((_, _) => Picture(bytes, type))));
        await Fails(() => cache.GetAsync(imageUrl), name + " rejected");
        Check(!Directory.EnumerateFiles(Path.Combine(Folder(name), "guide-images"), "*.img").Any(), name + " not cached");
    }
    using (var cache = new GuideImageCache(Folder("octets"), new HttpClient(new FakeHandler((_, _) => Picture(png, "application/octet-stream")))))
    {
        Check((await cache.GetAsync(imageUrl)).ContentType == "image/png", "generic binary response uses detected raster type");
    }
    using (var cache = new GuideImageCache(Folder("large-length"), new HttpClient(new FakeHandler((_, _) =>
    {
        var response = Picture();
        response.Content.Headers.ContentLength = 10 * 1024 * 1024 + 1;
        return response;
    })))) await Fails(() => cache.GetAsync(imageUrl), "declared oversize rejected", "10 MiB");

    using (var cache = new GuideImageCache(Folder("large-stream"), new HttpClient(new FakeHandler((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new RepeatingStream(10 * 1024 * 1024 + 1)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    })))) await Fails(() => cache.GetAsync(imageUrl), "unknown length bounded while streaming", "10 MiB");

    var badFolder = Folder("corrupt-cache");
    string badPath = Path.Combine(badFolder, "guide-images", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl))).ToLowerInvariant() + ".img");
    using (var cache = new GuideImageCache(badFolder, new HttpClient(new FakeHandler((_, _) => Picture()))))
    {
        File.WriteAllText(badPath, "not an image");
        cache.Offline = true;
        await Fails(() => cache.GetAsync(imageUrl), "corrupt cached image not served", "离线");
        cache.Offline = false;
        Check((await cache.GetAsync(imageUrl)).Bytes.SequenceEqual(png), "corrupt cache repaired online");
    }

    var trimFolder = Folder("trim");
    Directory.CreateDirectory(Path.Combine(trimFolder, "guide-images"));
    for (int index = 0; index < 21; index++)
    {
        var path = Path.Combine(trimFolder, "guide-images", index.ToString("x64") + ".img");
        using var file = File.Create(path);
        file.SetLength(10 * 1024 * 1024);
    }
    File.WriteAllText(Path.Combine(trimFolder, "guide-images", "unrelated.img"), "keep");
    using (var cache = new GuideImageCache(trimFolder, new HttpClient(new FakeHandler((_, _) => Picture()))))
    {
        Check(Directory.EnumerateFiles(Path.Combine(trimFolder, "guide-images"), "*.img").Where(p => Path.GetFileName(p).Length == 68)
            .Sum(p => new FileInfo(p).Length) <= 200L * 1024 * 1024, "old cache bounded to 200 MiB");
        Check(File.Exists(Path.Combine(trimFolder, "guide-images", "unrelated.img")), "cache trim does not remove unrelated files");
        await cache.GetAsync(imageUrl);
        Check(Directory.EnumerateFiles(Path.Combine(trimFolder, "guide-images"), "*.img").Where(p => Path.GetFileName(p).Length == 68)
            .Sum(p => new FileInfo(p).Length) <= 200L * 1024 * 1024, "new image keeps cache within limit");
    }

    if (args.Contains("--live"))
    {
        const string liveUrl = "https://patchwiki.biligame.com/images/stardewvalley/3/3f/lk72p0f9mg1yjx4tfiky8comhj5m2fa.png";
        using var cache = new GuideImageCache(Folder("live"));
        var picture = await cache.GetAsync(liveUrl);
        Check(picture.ContentType == "image/png" && picture.Bytes.Length > 100, "live public BWIKI image fetched and verified");
        cache.Offline = true;
        Check((await cache.GetAsync(liveUrl)).Bytes.SequenceEqual(picture.Bytes), "live image available offline after first view");
    }

    Console.WriteLine($"GuideImageChecks: {passed} checks passed.");
}
finally
{
    // Test-owned exact directory only; never touches launcher or game data.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(root, true);
}

sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var result = respond(request, Requests);
        result.RequestMessage ??= request;
        return Task.FromResult(result);
    }
}

sealed class RepeatingStream(int length) : Stream
{
    private int position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = Math.Min(count, length - position);
        Array.Fill(buffer, (byte)0, offset, read);
        position += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
