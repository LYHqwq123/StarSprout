using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout.Services;

/// <summary>Explicitly started EasyTier no-TUN rooms, exposing only Stardew's UDP 24642.</summary>
public sealed class MultiplayerService : IDisposable
{
    public const int GamePort = 24642;
    public const string DefaultPeer = "tcp://public.easytier.top:11010";
    public const string HostIp = "10.126.0.1";
    private readonly string directory;
    private readonly MultiplayerCoreStore store;
    private readonly Func<IMultiplayerRuntime> runtimeFactory;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object sync = new();
    private MultiplayerState state = new();
    private IMultiplayerRuntime? runtime;
    private MultiplayerRoom? room;
    private string instanceName = "";
    private int rpcPort;
    private bool disposed;
    public bool DownloadsOffline { get; set; }
    public bool Offline { get => DownloadsOffline; set => DownloadsOffline = value; }

    public MultiplayerService(string dataDirectory, MultiplayerCoreStore store, Func<IMultiplayerRuntime>? runtimeFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.runtimeFactory = runtimeFactory ?? (() => new EasyTierRuntime());
    }

    /// <summary>Local state only. Opening a sidebar never starts a core, queries a relay, or creates a room.</summary>
    public MultiplayerState GetState()
    {
        lock (sync)
        {
            var core = store.GetState();
            state.CoreInstalled = core.Installed; state.CoreVersion = core.Version;
            if (runtime != null && !runtime.IsRunning && state.Running)
            {
                state.Running = false; state.Status = "error"; state.PeerCount = 0; state.Peers.Clear();
                state.Error = "联机核心已退出。可查看日志后重新创建或加入房间。";
            }
            state.Logs = runtime?.GetRecentLogs().TakeLast(30).Select(Redact).ToList() ?? state.Logs;
            return Copy(state);
        }
    }

    public async Task<MultiplayerState> InstallCoreAsync(CancellationToken ct = default)
    {
        if (DownloadsOffline) throw new InvalidOperationException("当前为离线模式，请联网后下载联机核心；已安装核心仍可手动发起联机。");
        await operations.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync) if (runtime?.IsRunning == true) throw new InvalidOperationException("请先退出联机房间，再安装核心。");
            var installed = await store.DownloadAsync(ct);
            lock (sync) { state.Notice = installed.Message; state.Error = ""; }
            return GetState();
        }
        finally { operations.Release(); }
    }

    public Task<MultiplayerState> CreateRoomAsync(string nickname = "", CancellationToken ct = default)
        => StartAsync(CreateRoom(), "host", nickname, ct);

    public Task<MultiplayerState> JoinRoomAsync(string invitationCode, string nickname = "", CancellationToken ct = default)
        => StartAsync(ParseInvitation(invitationCode), "guest", nickname, ct);

    private async Task<MultiplayerState> StartAsync(MultiplayerRoom selected, string role, string nickname, CancellationToken ct)
    {
        nickname = NormalizeNickname(nickname, role);
        await operations.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync) if (runtime?.IsRunning == true) throw new InvalidOperationException("请先退出当前联机房间。");
            store.VerifyInstalled();
            if (role == "guest") EnsureGuestPortAvailable();
            string ip = role == "host" ? HostIp : CreateGuestIp();
            int managementPort = ReserveRpcPort();
            string instance = "starsprout-" + Guid.NewGuid().ToString("N");
            var next = runtimeFactory();
            try
            {
                next.Start(BuildStartInfo(store.CorePath, directory, selected, role, ip, managementPort, instance, nickname));
            }
            catch { next.Dispose(); throw; }
            lock (sync)
            {
                runtime?.Dispose(); runtime = next; room = selected; rpcPort = managementPort; instanceName = instance;
                state = new()
                {
                    Running = true, Status = "starting", Role = role, InvitationCode = EncodeInvitation(selected),
                    VirtualIp = ip, JoinAddress = role == "guest" ? "127.0.0.1" : "",
                    Notice = role == "host" ? "正在建立房间。请在游戏内选择合作 → 主持；把星芽邀请码私下发给好友。" : "正在连接房主。连接成功后，在游戏内合作 → 加入局域网，填写 127.0.0.1。"
                };
            }
            try { await Task.Delay(300, ct); return await RefreshRunningAsync(ct); }
            catch (OperationCanceledException) { StopOwned(); throw; }
        }
        finally { operations.Release(); }
    }

    public async Task<MultiplayerState> RefreshAsync(CancellationToken ct = default)
    {
        await operations.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await RefreshRunningAsync(ct); }
        finally { operations.Release(); }
    }

    private async Task<MultiplayerState> RefreshRunningAsync(CancellationToken ct)
    {
        IMultiplayerRuntime? active;
        string expectedIp;
        lock (sync) { active = runtime; expectedIp = state.VirtualIp; }
        if (active == null || !active.IsRunning) return GetState();
        try
        {
            var nodeJson = await active.QueryAsync(store.CliPath, rpcPort, instanceName, "node", ct);
            string actualIp = ParseNodeIp(nodeJson);
            if (actualIp != expectedIp) throw new InvalidDataException("核心返回的虚拟地址与当前房间不一致。");
            var peerJson = await active.QueryAsync(store.CliPath, rpcPort, instanceName, "peer", ct);
            var peers = ParsePeers(peerJson, actualIp);
            bool hasHost = peers.Any(peer => peer.VirtualIp == HostIp);
            lock (sync)
            {
                state.Peers = peers; state.PeerCount = peers.Count; state.Error = ""; state.Running = active.IsRunning;
                state.Status = !state.Running ? "error" : state.Role == "guest" ? hasHost ? "connected" : "connecting" : peers.Count > 0 ? "connected" : "hosting";
                state.Notice = state.Status switch
                {
                    "hosting" => "房间已创建，等待好友加入。房主仍需在游戏内选择合作 → 主持，并为好友准备小屋。",
                    "connected" when state.Role == "host" => "已发现同房间玩家。请在游戏内主持农场；联机模块不会代替游戏创建存档或小屋。",
                    "connected" => "已发现房主。游戏内选择合作 → 加入局域网，填写 127.0.0.1；游戏端口固定为 UDP 24642。",
                    "connecting" => "核心已启动，尚未发现房主。请确认双方使用相同星芽邀请码，房主没有退出房间。",
                    _ => "联机核心已退出。"
                };
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or TimeoutException or HttpRequestException or Win32Exception)
        {
            lock (sync)
            {
                state.Status = active.IsRunning ? "connecting" : "error";
                state.Error = Redact(ex.Message); state.PeerCount = 0; state.Peers.Clear(); state.Running = active.IsRunning;
                state.Notice = active.IsRunning ? "核心正在连接或暂时无法取得状态。点击刷新重试；不会把进程启动当作联机成功。" : "联机核心已退出，请重新加入。";
            }
        }
        return GetState();
    }

    public async Task<MultiplayerState> StopAsync(CancellationToken ct = default)
    {
        await operations.WaitAsync(ct);
        try { await Task.Run(StopOwned, CancellationToken.None); return GetState(); }
        finally { operations.Release(); }
    }

    private void StopOwned()
    {
        lock (sync)
        {
            if (runtime != null)
            {
                runtime.Stop(); state.Logs = runtime.GetRecentLogs().TakeLast(30).Select(Redact).ToList();
                runtime.Dispose(); runtime = null;
            }
            room = null; rpcPort = 0; instanceName = "";
            state = new() { Status = "idle", Notice = "已退出联机房间。", Logs = state.Logs };
        }
    }

    public static MultiplayerRoom CreateRoom() => new()
    {
        NetworkName = "starsprout-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), HostIp = HostIp, Peer = DefaultPeer
    };

    public static string EncodeInvitation(MultiplayerRoom room)
    {
        ValidateRoom(room);
        string json = JsonConvert.SerializeObject(new { version = room.Version, networkName = room.NetworkName, secret = room.Secret, hostIp = room.HostIp, peer = room.Peer });
        return "SG1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static MultiplayerRoom ParseInvitation(string code)
    {
        code = (code ?? "").Trim();
        if (code.StartsWith("U/", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("这是陶瓦的 Minecraft 邀请码。星露谷联机请使用房主生成的 SG1 星芽邀请码。");
        if (code.Length is < 10 or > 2048 || !Regex.IsMatch(code, "^SG1\\.[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)) throw new ArgumentException("请粘贴完整的 SG1 星芽联机邀请码。");
        try
        {
            string encoded = code[4..].Replace('-', '+').Replace('_', '/');
            byte[] bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            if (bytes.Length > 1024) throw new ArgumentException("邀请码内容过长。");
            using var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(bytes))) { MaxDepth = 3, DateParseHandling = DateParseHandling.None };
            var json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (json.Properties().Any(property => property.Name is not ("version" or "networkName" or "secret" or "hostIp" or "peer"))
                || json["version"]?.Type != JTokenType.Integer || new[] { "networkName", "secret", "hostIp", "peer" }.Any(key => json[key]?.Type != JTokenType.String)) throw new ArgumentException("邀请码结构无效。");
            var room = new MultiplayerRoom { Version = json.Value<int>("version"), NetworkName = json.Value<string>("networkName")!, Secret = json.Value<string>("secret")!, HostIp = json.Value<string>("hostIp")!, Peer = json.Value<string>("peer")! };
            ValidateRoom(room); return room;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or DecoderFallbackException or OverflowException)
        { throw new ArgumentException("邀请码已损坏，请重新复制完整的 SG1 星芽邀请码。"); }
    }

    private static void ValidateRoom(MultiplayerRoom room)
    {
        if (room == null || room.Version != 1 || !Regex.IsMatch(room.NetworkName ?? "", "^starsprout-[a-f0-9]{32}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(room.Secret ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) || room.HostIp != HostIp || room.Peer != DefaultPeer)
            throw new ArgumentException("邀请码的版本、房间或线路无效；请让房主重新生成星芽邀请码。");
    }

    public static ProcessStartInfo BuildStartInfo(string corePath, string workingDirectory, MultiplayerRoom room, string role, string virtualIp, int managementPort, string instanceName, string nickname)
    {
        ValidateRoom(room);
        if (role is not ("host" or "guest") || !ValidVirtualIp(virtualIp) || role == "host" && virtualIp != HostIp || role == "guest" && virtualIp == HostIp
            || managementPort is < 1024 or > 65535 || !Regex.IsMatch(instanceName, "^starsprout-[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new ArgumentException("联机进程参数无效。");
        nickname = NormalizeNickname(nickname, role);
        var info = new ProcessStartInfo(Path.GetFullPath(corePath)) { WorkingDirectory = Path.GetFullPath(workingDirectory), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // Ignore ambient EasyTier config-server, proxy, exit-node, listener and credential settings.
        foreach (var name in info.Environment.Keys.Where(key => key.StartsWith("ET_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        info.Environment["ET_NETWORK_SECRET"] = room.Secret;
        string[] args = { "--network-name", room.NetworkName, "--instance-name", instanceName, "--machine-id", instanceName, "--hostname", nickname,
            "--ipv4", virtualIp + "/16", "--no-tun", "true", "--disable-upnp", "true", "--bind-device", "false", "--private-mode", "true",
            "--disable-encryption", "false", "--encryption-algorithm", "aes-gcm", "--rpc-portal", "127.0.0.1:" + managementPort.ToString(CultureInfo.InvariantCulture),
            "--rpc-portal-whitelist", "127.0.0.1/32", "--listeners", "udp://0.0.0.0:0", "tcp://0.0.0.0:0", "--peers", room.Peer,
            "--tcp-whitelist", "0", "--udp-whitelist", role == "host" ? GamePort.ToString(CultureInfo.InvariantCulture) : "0", "--console-log-level", "warn" };
        foreach (string value in args) info.ArgumentList.Add(value);
        if (role == "guest") { info.ArgumentList.Add("--port-forward"); info.ArgumentList.Add($"udp://127.0.0.1:{GamePort}/{HostIp}:{GamePort}"); }
        return info;
    }

    public static string ParseNodeIp(string json)
    {
        var token = ParseJson(json);
        if (token is not JObject node || node["ipv4_addr"]?.Type != JTokenType.String) throw new InvalidDataException("联机核心没有返回有效的节点状态。");
        string ip = node.Value<string>("ipv4_addr")!.Split('/')[0];
        if (!ValidVirtualIp(ip)) throw new InvalidDataException("联机核心没有返回当前房间的虚拟地址。");
        return ip;
    }

    public static List<MultiplayerPeer> ParsePeers(string json, string ownIp)
    {
        if (ParseJson(json) is not JArray list || list.Count > 1024) throw new InvalidDataException("联机核心没有返回有效的玩家列表。");
        return list.OfType<JObject>().Where(peer => peer.Value<string>("cost")?.Equals("Local", StringComparison.OrdinalIgnoreCase) != true)
            .Where(peer => ValidVirtualIp(peer.Value<string>("ipv4") ?? "") && peer.Value<string>("ipv4") != ownIp)
            .Select(peer => new MultiplayerPeer
            {
                Hostname = SafeText(peer.Value<string>("hostname") ?? "星芽玩家", 80), VirtualIp = peer.Value<string>("ipv4")!,
                ConnectionType = SafeText(peer.Value<string>("cost") ?? "unknown", 40),
                LatencyMs = double.TryParse(peer.Value<string>("lat_ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out double latency) && double.IsFinite(latency) && latency >= 0 ? latency : null
            }).GroupBy(peer => peer.VirtualIp, StringComparer.Ordinal).Select(group => group.First()).Take(64).ToList();
    }

    private static JToken ParseJson(string value)
    {
        if (value.Length > 1024 * 1024) throw new InvalidDataException("联机状态超过大小上限。");
        using var reader = new JsonTextReader(new StringReader(value)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
        return JToken.ReadFrom(reader);
    }
    private static string CreateGuestIp() => $"10.126.{RandomNumberGenerator.GetInt32(1, 255)}.{RandomNumberGenerator.GetInt32(1, 255)}";
    private static bool ValidVirtualIp(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != value) return false;
        byte[] bytes = ip.GetAddressBytes(); return bytes[0] == 10 && bytes[1] == 126 && bytes[3] is > 0 and < 255;
    }
    private static int ReserveRpcPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    private static void EnsureGuestPortAvailable()
    {
        try { using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true }; socket.Bind(new IPEndPoint(IPAddress.Loopback, GamePort)); }
        catch (SocketException) { throw new InvalidOperationException("本机 UDP 24642 已被占用。请先退出其他联机工具或正在主持的农场，再加入房间。"); }
    }
    private static string NormalizeNickname(string nickname, string role)
    {
        nickname = (nickname ?? "").Trim();
        if (nickname.Length > 40 || nickname.Any(char.IsControl)) throw new ArgumentException("联机昵称最多 40 字，不能包含控制字符。");
        return nickname.Length > 0 ? nickname : role == "host" ? "星芽房主" : "星芽访客";
    }
    private string Redact(string value)
    {
        if (room != null) { value = value.Replace(room.Secret, "[已隐藏]", StringComparison.OrdinalIgnoreCase); value = value.Replace(EncodeInvitation(room), "[邀请码已隐藏]", StringComparison.Ordinal); }
        return SafeText(value, 1200);
    }
    private static string SafeText(string text, int limit) => new(text.Where(c => !char.IsControl(c) || c == '\t').Take(limit).ToArray());
    private static MultiplayerState Copy(MultiplayerState value) => new()
    {
        CoreInstalled = value.CoreInstalled, CoreVersion = value.CoreVersion, Status = value.Status, Role = value.Role, InvitationCode = value.InvitationCode,
        VirtualIp = value.VirtualIp, JoinAddress = value.JoinAddress, PeerCount = value.PeerCount, Running = value.Running, Notice = value.Notice, Error = value.Error,
        Logs = value.Logs.ToList(), Peers = value.Peers.Select(peer => new MultiplayerPeer { Hostname = peer.Hostname, VirtualIp = peer.VirtualIp, LatencyMs = peer.LatencyMs, ConnectionType = peer.ConnectionType }).ToList()
    };
    public void Dispose() { if (disposed) return; disposed = true; StopOwned(); }

    private sealed class EasyTierRuntime : IMultiplayerRuntime
    {
        private readonly Queue<string> logs = new();
        private readonly object logSync = new();
        private Process? process;
        public bool IsRunning { get { try { return process != null && !process.HasExited; } catch (InvalidOperationException) { return false; } } }
        public void Start(ProcessStartInfo startInfo)
        {
            if (process != null) throw new InvalidOperationException("核心实例已经启动。");
            process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, args) => Capture(args.Data); process.ErrorDataReceived += (_, args) => Capture(args.Data);
            if (!process.Start()) throw new InvalidOperationException("未能启动 EasyTier 联机核心。");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        private void Capture(string? line)
        {
            if (line == null) return;
            lock (logSync) { logs.Enqueue(line[..Math.Min(line.Length, 4000)]); while (logs.Count > 100) logs.Dequeue(); }
        }
        public IReadOnlyList<string> GetRecentLogs() { lock (logSync) return logs.ToList(); }
        public async Task<string> QueryAsync(string cliPath, int rpcPort, string instanceName, string command, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var start = new ProcessStartInfo(cliPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (string argument in new[] { "-p", "127.0.0.1:" + rpcPort.ToString(CultureInfo.InvariantCulture), "-n", instanceName, "-o", "json", command, command == "peer" ? "list" : "info" }) start.ArgumentList.Add(argument);
            using var cli = Process.Start(start) ?? throw new InvalidOperationException("未能读取联机核心状态。");
            try
            {
                var output = ReadBounded(cli.StandardOutput, timeout.Token); var errors = ReadBounded(cli.StandardError, timeout.Token);
                await cli.WaitForExitAsync(timeout.Token); string stdout = await output; string stderr = await errors;
                if (cli.ExitCode != 0) throw new InvalidOperationException("联机状态读取失败。" + SafeText(stderr, 400));
                return stdout;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("读取联机状态超时，请稍后刷新。"); }
            finally { try { if (!cli.HasExited) cli.Kill(true); } catch (InvalidOperationException) { } }
        }
        private static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
        {
            var result = new StringBuilder(); var chunk = new char[8192]; int count;
            while ((count = await reader.ReadAsync(chunk, ct)) > 0) { if (result.Length + count > 1024 * 1024) throw new InvalidDataException("核心状态输出过大。"); result.Append(chunk, 0, count); }
            return result.ToString();
        }
        public void Stop()
        {
            try { if (process != null && !process.HasExited) { process.Kill(true); process.WaitForExit(2000); } }
            catch (InvalidOperationException) { }
        }
        public void Dispose() { Stop(); process?.Dispose(); process = null; }
    }
}
