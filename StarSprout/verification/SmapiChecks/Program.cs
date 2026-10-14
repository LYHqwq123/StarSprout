using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using StarSprout.Services;

var count = 0;
void Check(bool result, string message) { if (!result) throw new Exception(message); ++count; Console.WriteLine("PASS " + message); }
async Task Reject(Func<Task> operation, string message)
{
    try { await operation(); }
    catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or HttpRequestException or OperationCanceledException)
    { Check(true, message); return; }
    throw new Exception(message + " did not reject");
}
byte[] Zip(params (string Name, string Contents)[] entries)
{
    using var result = new MemoryStream();
    using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true))
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    return result.ToArray();
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var root = Path.Combine(Path.GetTempPath(), "StarSprout-SmapiChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args.Length == 2 && args[0] == "--sample")
    {
        var sample = File.ReadAllBytes(Path.GetFullPath(args[1]));
        Check(Hash(sample) == SmapiDownloadService.OfficialSha256, "Real domestic ZIP matches official release SHA256 exactly");
        using var sampleClient = new HttpClient(new FakeHandler { Reply = (_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(sample) } });
        using var sampleService = new SmapiDownloadService(root, sampleClient);
        var sampleResult = await sampleService.DownloadAsync();
        Check(File.Exists(sampleResult.InstallerPath) && File.Exists(Path.Combine(sampleResult.Directory, "internal", "windows", "install.dat")), "Real SMAPI Windows installer safely extracted without running");
        Check(!sampleResult.FromCache && !sampleResult.UsedFallback && sampleResult.SourceId == "domestic", "Real domestic installer uses domestic source");
        Console.WriteLine($"SMAPI sample checks passed: {count}");
        return;
    }
    using (var defaults = new SmapiDownloadService(root))
    {
        var state = defaults.GetState();
        Check(state.Sources.Count == 2 && state.Sources[0].Id == "domestic", "Domestic source is first and official fallback remains available");
        Check(state.Sources.All(x => x.Version == "4.5.2" && x.MinimumGameVersion == "1.6.14" && x.Sha256 == SmapiDownloadService.OfficialSha256), "Pinned release and game requirements shown for both sources");
        state.Sources[0].DownloadUrl = "https://evil.test/tampered.zip";
        Check(defaults.GetState().Sources[0].DownloadUrl.StartsWith("https://dmod.3dmgame.com/"), "State cannot mutate download source");
    }

    var package = Zip(("SMAPI 4.5.2 installer/install on Windows.bat", "@echo fixture; do not execute"),
        ("SMAPI 4.5.2 installer/internal/windows/install.dat", "unexecuted fixture"),
        ("SMAPI 4.5.2 installer/README.txt", "fixture"));
    List<SmapiDownloadSource> Sources(byte[] payload) => new()
    {
        new() { Id="domestic", Name="国内 · 3DM", Version="4.5.2", Sha256=Hash(payload), DownloadUrl="https://dmod.3dmgame.com/static/upload/resource2/fixture.zip" },
        new() { Id="github", Name="官方 · GitHub", Version="4.5.2", Sha256=Hash(payload), DownloadUrl="https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/fixture.zip" }
    };
    var handler = new FakeHandler { Reply = (_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(package) } };
    using var client = new HttpClient(handler);
    using var service = new SmapiDownloadService(Path.Combine(root,"happy"), client, Sources(package));
    var phases = new List<string>();
    service.Progress += p => phases.Add(p.Phase);
    var result = await service.DownloadAsync();
    Check(handler.Calls.Count == 1 && handler.Calls[0].Host == "dmod.3dmgame.com", "Download button fetches domestic ZIP directly");
    Check(File.Exists(result.Path) && File.Exists(result.InstallerPath) && result.Sha256 == Hash(package), "Hash verified and installer prepared in private cache");
    Check(phases.Contains("downloading") && phases.Contains("verifying") && phases.Contains("extracting") && phases.Last() == "completed", "Download verification and preparation progress reported");
    Check(!result.FromCache && !result.UsedFallback && result.Message.Contains("1.6.14"), "Prepared package shows compatibility and manual installer action");
    Check(handler.CredentialsSent == false, "Installer request sends no credentials");
    service.Offline = true;
    var cached = await service.DownloadAsync();
    Check(cached.FromCache && handler.Calls.Count == 1, "Verified cache works offline without network");
    File.WriteAllText(result.Path, "tampered cache");
    await Reject(() => service.DownloadAsync(), "Offline corrupted cache rejected");
    service.Offline = false;
    await service.DownloadAsync();
    Check(handler.Calls.Count == 2, "Corrupted cache replaced by verified fresh download");
    await Reject(() => service.DownloadAsync("evil"), "Unknown source rejected");
    Check(handler.Calls.Count == 2, "Invalid source sends no request");

    using var fallbackHandler = new FakeHandler { Reply = (request, _) => request.RequestUri!.Host == "dmod.3dmgame.com"
        ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
        : new(HttpStatusCode.OK) { Content = new ByteArrayContent(package) } };
    using var fallbackClient = new HttpClient(fallbackHandler);
    using var fallbackService = new SmapiDownloadService(Path.Combine(root,"fallback"), fallbackClient, Sources(package));
    var fallback = await fallbackService.DownloadAsync();
    Check(fallback.UsedFallback && fallback.SourceId == "github" && fallbackHandler.Calls.Count == 2, "Network failure falls back to same verified official release");

    using var badHashHandler = new FakeHandler { Reply = (_, _) => new(HttpStatusCode.OK) { Content = new StringContent("wrong ZIP") } };
    using var badHashClient = new HttpClient(badHashHandler);
    using var badHashService = new SmapiDownloadService(Path.Combine(root,"hash"), badHashClient, Sources(package));
    await Reject(() => badHashService.DownloadAsync(), "Mismatched official hash rejected before extraction");
    Check(badHashHandler.Calls.Count == 1 && !Directory.GetFiles(Path.Combine(root,"hash"),"*.part",SearchOption.AllDirectories).Any(), "Hash mismatch cannot silently fall back and leaves no partial file");

    using var redirectHandler = new FakeHandler { Reply = (_, _) => new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://127.0.0.1/private") } } };
    using var redirectClient = new HttpClient(redirectHandler);
    using var redirectService = new SmapiDownloadService(Path.Combine(root,"redirect"), redirectClient, Sources(package));
    await Reject(() => redirectService.DownloadAsync(), "Redirect outside verified hosts rejected");
    Check(redirectHandler.Calls.Count == 1, "Unsafe redirect target never requested");

    using var githubRedirectHandler = new FakeHandler { Reply = (_, number) => number == 1
        ? new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/example/fixture.zip?signature=test") } }
        : new(HttpStatusCode.OK) { Content = new ByteArrayContent(package) } };
    using var githubRedirectClient = new HttpClient(githubRedirectHandler);
    using var githubRedirectService = new SmapiDownloadService(Path.Combine(root,"allowedredirect"), githubRedirectClient, Sources(package));
    var github = await githubRedirectService.DownloadAsync("github");
    Check(githubRedirectHandler.Calls.Count == 2 && github.SourceId == "github", "Official release CDN redirects supported");

    foreach (var (name, file) in new[] { ("Traversal", "../outside.txt"), ("Windows device", "SMAPI/CON.txt"), ("Alternate stream", "SMAPI/install.txt:payload"), ("Rooted", "/outside.txt") })
    {
        var unsafePackage = Zip(("SMAPI/install on Windows.bat","fixture"),("SMAPI/internal/windows/install.dat","fixture"),(file,"unsafe"));
        using var unsafeClient = new HttpClient(new FakeHandler { Reply = (_,_) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(unsafePackage) } });
        using var unsafeService = new SmapiDownloadService(Path.Combine(root,name),unsafeClient,Sources(unsafePackage));
        await Reject(() => unsafeService.DownloadAsync(), name + " path cannot escape installer cache");
    }
    var wrongPackage = Zip(("Mod/manifest.json","{}"),("Mod/content.json","{}"));
    using var wrongClient = new HttpClient(new FakeHandler { Reply = (_,_) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(wrongPackage) } });
    using var wrongService = new SmapiDownloadService(Path.Combine(root,"wrong"),wrongClient,Sources(wrongPackage));
    await Reject(() => wrongService.DownloadAsync(), "Normal Mod ZIP cannot masquerade as SMAPI installer");
    var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await Reject(() => service.DownloadAsync("domestic",cancellation.Token), "Canceled operation stops before network");
    Check(!File.Exists(Path.Combine(root,"outside.txt")), "No fixture writes outside isolated cache");
    Console.WriteLine($"SMAPI checks passed: {count}");
}
finally
{
    var safeRoot = Path.GetFullPath(root);
    if (safeRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(safeRoot).StartsWith("StarSprout-SmapiChecks-"))
        Directory.Delete(safeRoot, true);
}

sealed class FakeHandler : HttpMessageHandler
{
    public required Func<HttpRequestMessage, int, HttpResponseMessage> Reply { get; init; }
    public List<Uri> Calls { get; } = new();
    public bool CredentialsSent { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add(request.RequestUri!);
        CredentialsSent |= request.Headers.Contains("Cookie") || request.Headers.Contains("Authorization");
        return Task.FromResult(Reply(request,Calls.Count));
    }
}
