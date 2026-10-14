using System.Diagnostics;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using StarSprout.Services;

string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException) { checks++; return; }
    throw new Exception("FAIL: expected rejection: " + message);
}
string EncodeRaw(object value) => "SG1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

var room = MultiplayerService.CreateRoom();
var second = MultiplayerService.CreateRoom();
Check(room.Secret.Length == 32 && room.NetworkName.StartsWith("starsprout-") && room.Secret != second.Secret && room.NetworkName != second.NetworkName, "rooms have independent 128-bit random names and secrets");
string invitation = MultiplayerService.EncodeInvitation(room);
var decoded = MultiplayerService.ParseInvitation(invitation);
Check(invitation.StartsWith("SG1.") && invitation.Length < 1024 && decoded.Secret == room.Secret && decoded.NetworkName == room.NetworkName, "bounded StarSprout invitation round trips credentials");
Check(decoded.Peer == MultiplayerService.DefaultPeer && decoded.HostIp == "10.126.0.1", "invitation cannot choose arbitrary remote target or relay");
foreach (string bad in new[] { "", "U/1234-5678-9012-3456", "SG1.%%%%", new string('A', 2049), EncodeRaw(new { version = 2, networkName = room.NetworkName, secret = room.Secret, hostIp = room.HostIp, peer = room.Peer }), EncodeRaw(new { version = 1, networkName = room.NetworkName, secret = room.Secret, hostIp = "127.0.0.1", peer = room.Peer }), EncodeRaw(new { version = 1, networkName = room.NetworkName, secret = room.Secret, hostIp = room.HostIp, peer = "tcp://attacker.invalid:1234" }), EncodeRaw(new { version = 1, networkName = room.NetworkName, secret = room.Secret, hostIp = room.HostIp, peer = room.Peer, configServer = "udp://attacker.invalid" }), EncodeRaw(new { version = 1, networkName = "--config-file evil", secret = room.Secret, hostIp = room.HostIp, peer = room.Peer }), EncodeRaw(new { version = 1, networkName = room.NetworkName, secret = "weak", hostIp = room.HostIp, peer = room.Peer }) })
    await Reject(() => Task.FromResult(MultiplayerService.ParseInvitation(bad)), "malformed or unsafe invitation");
string duplicateJson = "{\"version\":1,\"version\":1,\"networkName\":\"" + room.NetworkName + "\",\"secret\":\"" + room.Secret + "\",\"hostIp\":\"10.126.0.1\",\"peer\":\"" + room.Peer + "\"}";
await Reject(() => Task.FromResult(MultiplayerService.ParseInvitation("SG1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(duplicateJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_'))), "duplicate JSON fields cannot override invitation validation");
Environment.SetEnvironmentVariable("ET_CONFIG_SERVER", "udp://unwanted.invalid:22020/admin");
var startInfo = MultiplayerService.BuildStartInfo(Path.Combine(root, "core.exe"), root, room, "host", room.HostIp, 25001, "starsprout-" + new string('a', 32), "测试昵称 \" $(nothing)");
Environment.SetEnvironmentVariable("ET_CONFIG_SERVER", null);
var argv = startInfo.ArgumentList.ToArray();
Check(!startInfo.UseShellExecute && startInfo.CreateNoWindow && startInfo.RedirectStandardOutput && startInfo.RedirectStandardError, "child starts hidden without shell expansion");
Check(!argv.Contains(room.Secret) && startInfo.Environment["ET_NETWORK_SECRET"] == room.Secret && !startInfo.Environment.ContainsKey("ET_CONFIG_SERVER"), "secret stays out of command line and inherited EasyTier environment is removed");
Check(argv.Contains("--no-tun") && argv.Contains("--disable-upnp") && argv.Contains("--private-mode") && !argv.Contains("--proxy-networks") && !argv.Contains("--enable-exit-node") && !argv.Contains("--accept-dns"), "no virtual adapter, UPnP, exported subnet, exit route or DNS changes");
Check(argv[Array.IndexOf(argv, "--rpc-portal") + 1] == "127.0.0.1:25001" && argv[Array.IndexOf(argv, "--rpc-portal-whitelist") + 1] == "127.0.0.1/32", "management RPC is loopback-only");
Check(argv[Array.IndexOf(argv, "--tcp-whitelist") + 1] == "0" && argv[Array.IndexOf(argv, "--udp-whitelist") + 1] == "24642", "host permits only game UDP, rejects normal TCP ports");
Check(argv.Contains("测试昵称 \" $(nothing)"), "nickname remains one literal argument");
var guestArgs = MultiplayerService.BuildStartInfo(Path.Combine(root, "core.exe"), root, room, "guest", "10.126.9.9", 25002, "starsprout-" + new string('b', 32), "").ArgumentList.ToArray();
Check(guestArgs.Contains("udp://127.0.0.1:24642/10.126.0.1:24642") && guestArgs[Array.IndexOf(guestArgs, "--udp-whitelist") + 1] == "0", "guest forwards local game UDP without exposing local services");
await Reject(() => Task.FromResult(MultiplayerService.BuildStartInfo("core.exe", root, room, "host", "8.8.8.8", 25002, "starsprout-" + new string('b', 32), "")), "external virtual IP");
await Reject(() => Task.FromResult(MultiplayerService.BuildStartInfo("core.exe", root, room, "host", room.HostIp, 25002, "bad instance", "")), "invalid instance identity");
await Reject(() => Task.FromResult(MultiplayerService.BuildStartInfo("core.exe", root, room, "host", room.HostIp, 25002, "starsprout-" + new string('b', 32), "bad\nname")), "nickname control characters");
Check(MultiplayerService.ParseNodeIp("{\"ipv4_addr\":\"10.126.0.1/16\",\"config\":\"private-secret-not-to-return\"}") == "10.126.0.1", "node parsing returns only expected IP, not credential-bearing config");
await Reject(() => Task.FromResult(MultiplayerService.ParseNodeIp("{\"ipv4_addr\":\"127.0.0.1\"}")), "foreign node address");
string peersJson = "[{\"ipv4\":\"10.126.1.2\",\"hostname\":\"self\",\"cost\":\"Local\"},{\"ipv4\":\"\",\"hostname\":\"PublicServer\",\"cost\":\"p2p\"},{\"ipv4\":\"10.126.0.1\",\"hostname\":\"host\",\"cost\":\"p2p\",\"lat_ms\":\"2.45\"},{\"ipv4\":\"10.126.0.1\",\"hostname\":\"duplicate\",\"cost\":\"p2p\"}]";
var peers = MultiplayerService.ParsePeers(peersJson, "10.126.1.2");
Check(peers.Count == 1 && peers[0].VirtualIp == room.HostIp && peers[0].LatencyMs == 2.45 && peers[0].ConnectionType == "p2p", "real CLI peers format excludes self and public relays, deduplicates and parses latency invariantly");
await Reject(() => Task.FromResult(MultiplayerService.ParsePeers("{\"bad\":true}", "10.126.1.2")), "malformed peers are not success");

// No public room or real child process is ever created by this suite.
var zip = args.FirstOrDefault(path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
if (zip != null)
{
    using var handler = new ZipHandler(File.ReadAllBytes(Path.GetFullPath(zip)));
    using var http = new HttpClient(handler);
    using var store = new MultiplayerCoreStore(Path.Combine(root, "core"), http);
    var fake = new FakeRuntime();
    using var service = new MultiplayerService(Path.Combine(root, "runtime"), store, () => fake);
    Check(service.GetState().Status == "idle" && !service.GetState().Running && handler.Calls == 0 && fake.Starts == 0, "opening state never downloads, launches or connects");
    service.DownloadsOffline = true;
    await Reject(() => service.InstallCoreAsync(), "offline download prevention");
    service.DownloadsOffline = false;
    await service.InstallCoreAsync();
    Check(service.GetState().CoreInstalled && handler.Calls == 1 && fake.Starts == 0, "explicit download installs verified core without executing it");
    var started = await service.CreateRoomAsync("Test Host");
    Check(started.Status == "hosting" && started.Running && started.PeerCount == 0, "running core plus zero players becomes hosting rather than connected");
    Check(started.Logs.All(line => !line.Contains(fake.StartInfo!.Environment["ET_NETWORK_SECRET"]!)), "core logs redact network secret");
    fake.IncludePeer = true;
    Check((await service.RefreshAsync()).Status == "connected" && service.GetState().PeerCount == 1, "connected status requires actual parsed same-room peer");
    fake.QueryError = true;
    Check((await service.RefreshAsync()).Status == "connecting" && service.GetState().PeerCount == 0 && service.GetState().Error.Length > 0, "RPC failure clears stale connected status");
    fake.QueryError = false;
    await Reject(() => service.CreateRoomAsync(), "second room cannot replace an active session silently");
    await service.StopAsync();
    Check(!service.GetState().Running && service.GetState().InvitationCode.Length == 0 && fake.Stops > 0, "explicit stop terminates only owned runtime and removes live invitation");
    service.DownloadsOffline = true;
    fake.IncludePeer = true;
    var joined = await service.JoinRoomAsync(invitation);
    Check(joined.Status == "connected" && joined.JoinAddress == "127.0.0.1" && joined.Role == "guest", "installed core supports explicit remote co-op for independent offline game profile");
    fake.Running = false;
    Check(service.GetState().Status == "error" && service.GetState().PeerCount == 0, "core crash cannot leave false connected state");
    await service.StopAsync();
    Check(!Directory.EnumerateFiles(root, "*.toml", SearchOption.AllDirectories).Any(), "room secret is never persisted in a TOML file");
}
Console.WriteLine($"PASS: {checks} multiplayer coordinator checks. No public connections or real core executions. Fixtures: {root}");

sealed class ZipHandler(byte[] bytes) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (request.RequestUri!.AbsoluteUri != MultiplayerCoreStore.DownloadUrl) throw new Exception("Unexpected download target");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }
}

sealed class FakeRuntime : IMultiplayerRuntime
{
    public bool Running { get; set; }
    public bool IsRunning => Running;
    public bool IncludePeer { get; set; }
    public bool QueryError { get; set; }
    public ProcessStartInfo? StartInfo { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    private string Ip => StartInfo!.ArgumentList[StartInfo.ArgumentList.IndexOf("--ipv4") + 1].Split('/')[0];
    public void Start(ProcessStartInfo info) { StartInfo = info; Running = true; Starts++; }
    public Task<string> QueryAsync(string cliPath, int rpcPort, string instanceName, string command, CancellationToken cancellationToken)
    {
        if (QueryError) throw new InvalidOperationException("RPC unavailable");
        if (command == "node") return Task.FromResult(JsonConvert.SerializeObject(new { ipv4_addr = Ip + "/16", config = "hidden" }));
        var peers = new List<object> { new { ipv4 = Ip, hostname = "self", cost = "Local", lat_ms = "-" } };
        if (IncludePeer) peers.Add(new { ipv4 = Ip == "10.126.0.1" ? "10.126.9.9" : "10.126.0.1", hostname = "friend", cost = "p2p", lat_ms = "1.25" });
        return Task.FromResult(JsonConvert.SerializeObject(peers));
    }
    public IReadOnlyList<string> GetRecentLogs() => new[] { "core ready", "network_secret=" + StartInfo?.Environment["ET_NETWORK_SECRET"] };
    public void Stop() { Running = false; Stops++; }
    public void Dispose() => Stop();
}
