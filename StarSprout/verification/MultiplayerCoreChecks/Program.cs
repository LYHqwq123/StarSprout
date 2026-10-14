using System.Net;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using StarSprout.Services;

string root = Path.Combine(AppContext.BaseDirectory, "core-check-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
async Task Fails(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception) { passed++; return; }
    throw new Exception("FAIL: " + name);
}
string Folder(string name) => Path.Combine(root, name);
HttpResponseMessage Redirect(string url)
{
    var response = new HttpResponseMessage(HttpStatusCode.Found);
    response.Headers.Location = new Uri(url);
    return response;
}
try
{
    var noNetwork = new FakeHandler(_ => throw new Exception("Unexpected network"));
    using (var store = new MultiplayerCoreStore(Folder("initial"), new HttpClient(noNetwork)))
    {
        Check(!store.GetState().Installed && !store.IsInstalled, "initial core not installed");
        Check(store.GetState().Version == "2.6.4", "pinned target version shown");
        Check(store.CorePath.StartsWith(Path.GetFullPath(Folder("initial")), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(store.CliPath) == "easytier-cli.exe", "core paths remain under owned data directory");
        Check(noNetwork.Requests == 0, "constructor and status never connect to internet");
        await Fails(() => Task.Run(() => store.VerifyInstalled()), "uninstalled core cannot launch");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Fails(() => store.DownloadAsync(cancelled.Token), "cancelled install refused");
        Check(noNetwork.Requests == 0, "cancelled install never downloads");
    }
    foreach (var url in new[] { "https://evil.example/core.zip", "http://release-assets.githubusercontent.com/x", "https://127.0.0.1/x", "https://release-assets.githubusercontent.com:444/x", "https://u:p@release-assets.githubusercontent.com/x" })
    {
        var handler = new FakeHandler(_ => Redirect(url));
        using var store = new MultiplayerCoreStore(Folder(Guid.NewGuid().ToString("N")), new HttpClient(handler));
        await Fails(() => store.DownloadAsync(), "nonofficial redirect blocked");
        Check(handler.Requests == 1 && !store.IsInstalled, "blocked redirect makes no unsafe request or installed core");
    }
    var loop = new FakeHandler(_ => Redirect(MultiplayerCoreStore.DownloadUrl));
    using (var store = new MultiplayerCoreStore(Folder("loop"), new HttpClient(loop)))
    {
        await Fails(() => store.DownloadAsync(), "redirect chain bounded");
        Check(loop.Requests == 6, "at most five redirects followed");
    }
    using (var store = new MultiplayerCoreStore(Folder("small"), new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) }))))
    {
        await Fails(() => store.DownloadAsync(), "incorrect declared archive size rejected");
        Check(!store.IsInstalled && !Directory.EnumerateFileSystemEntries(Folder("small")).Any(), "failed download leaves no installed or temporary files");
    }
    using (var store = new MultiplayerCoreStore(Folder("oversize"), new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(MultiplayerCoreStore.ArchiveBytes + 1)) }))))
    {
        await Fails(() => store.DownloadAsync(), "streamed archive bound enforced");
        Check(!store.IsInstalled, "oversized download cannot install");
    }
    using (var store = new MultiplayerCoreStore(Folder("wronghash"), new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(MultiplayerCoreStore.ArchiveBytes)) }))))
    {
        await Fails(() => store.DownloadAsync(), "correct length but wrong SHA256 rejected");
        Check(!store.IsInstalled && !Directory.EnumerateFileSystemEntries(Folder("wronghash")).Any(), "unverified archive is never extracted");
    }

    int archiveIndex = Array.IndexOf(args, "--archive");
    if (archiveIndex >= 0 && archiveIndex + 1 < args.Length)
    {
        string archive = Path.GetFullPath(args[archiveIndex + 1]);
        Check(new FileInfo(archive).Length == MultiplayerCoreStore.ArchiveBytes && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).Equals(MultiplayerCoreStore.ArchiveSha256, StringComparison.OrdinalIgnoreCase), "local official fixture digest verified");
        var handler = new FakeHandler(request => request.RequestUri!.Host == "github.com" ? Redirect("https://release-assets.githubusercontent.com/test-official-archive")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(archive)) });
        using var store = new MultiplayerCoreStore(Folder("official"), new HttpClient(handler));
        var installed = await store.DownloadAsync();
        Check(installed.Installed && store.VerifyInstalled().Installed, "official archive installs and revalidates");
        Check(handler.Requests == 2, "official asset redirect accepted");
        var installedDirectory = Path.GetDirectoryName(store.CorePath)!;
        Check(Directory.GetFiles(installedDirectory).Select(Path.GetFileName).Order().SequenceEqual(new[] { "easytier-cli.exe", "easytier-core.exe", "installed.json", "LICENSE-EasyTier.txt", "LICENSE-GPL-3.0.txt", "NOTICE.txt" }.Order()), "only core CLI and license metadata extracted; no driver DLL web executable");
        Check(File.ReadAllText(Path.Combine(installedDirectory, "LICENSE-EasyTier.txt")).Contains("GNU LESSER GENERAL PUBLIC LICENSE"), "LGPL license included");
        Check(File.ReadAllText(Path.Combine(installedDirectory, "NOTICE.txt")).Contains("refs/tags/v2.6.4.zip"), "exact corresponding source linked");
        await store.DownloadAsync();
        Check(handler.Requests == 2, "valid core is not redownloaded");
        Check(!Directory.EnumerateFileSystemEntries(Folder("official")).Any(path => Path.GetFileName(path).StartsWith('.')), "successful install leaves no staging or backup files");

        using (var core = new FileStream(store.CorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { int first = core.ReadByte(); core.Position = 0; core.WriteByte((byte)(first ^ 1)); }
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installedDirectory, "installed.json")));
        manifest["Files"]!.First(file => file.Value<string>("Name") == "easytier-core.exe")["Sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(store.CorePath))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(installedDirectory, "installed.json"), manifest.ToString());
        Check(!store.IsInstalled, "editing both binary and local manifest cannot bypass embedded hash pins");
        await Fails(() => Task.Run(() => store.VerifyInstalled()), "tampered executable refused before launch");
        await store.DownloadAsync();
        Check(store.IsInstalled, "tampered core repaired atomically from original archive");
        File.WriteAllText(Path.Combine(installedDirectory, "unexpected.dll"), "not a valid library");
        Check(!store.IsInstalled, "unexpected side-load DLL invalidates core directory");
        await store.DownloadAsync();
        Check(store.IsInstalled && !File.Exists(Path.Combine(installedDirectory, "unexpected.dll")), "reinstall removes only extra file inside owned core directory");
        string sentinel = Path.Combine(Folder("official"), "user-sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        Directory.CreateDirectory(Path.Combine(installedDirectory, "unexpected-subfolder"));
        await Fails(() => store.DownloadAsync(), "unexpected nested directory prevents destructive replacement");
        Check(File.Exists(sentinel) && Directory.Exists(Path.Combine(installedDirectory, "unexpected-subfolder")), "failed replacement preserves existing directory and unrelated data");
    }
    Console.WriteLine($"MultiplayerCoreChecks: {passed} checks passed; no EasyTier processes were executed.");
}
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var response = respond(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}

sealed class ZeroStream(long length) : Stream
{
    private long position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = (int)Math.Min(count, length - position);
        Array.Clear(buffer, offset, read);
        position += read;
        return read;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
