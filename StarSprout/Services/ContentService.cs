using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout.Services;

/// <summary>Author/community ZIP downloads and portable offline archives; never downloads the game.</summary>
public sealed class ContentService : IDisposable
{
    private const long MaxArchive = 512L * 1024 * 1024;
    private const long MaxBundle = 1024L * 1024 * 1024;
    private const int MaxJson = 2 * 1024 * 1024;
    private static readonly Regex SafeId = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);
    private readonly string directory, cache, statePath, catalogPath;
    private StardewService stardew;
    public void SetInstallation(StardewService value) => stardew = value ?? throw new ArgumentNullException(nameof(value));
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly object stateLock = new();
    private readonly SemaphoreSlim mutation = new(1, 1);
    private ContentState state;
    public event Action<DownloadProgress>? Progress;
    public void ReportPhase(string name, string phase) => Progress?.Invoke(new() { Name = name, Phase = phase });

    public ContentService(string dataDirectory, StardewService stardew, HttpClient? httpClient = null)
    {
        directory = Path.GetFullPath(Path.Combine(dataDirectory, "Content"));
        cache = Path.Combine(directory, "Archives");
        statePath = Path.Combine(directory, "state.json");
        catalogPath = Path.Combine(directory, "catalog-cache.json");
        NoLinks(directory);
        Directory.CreateDirectory(cache);
        this.stardew = stardew;
        ownsClient = httpClient == null;
        client = httpClient ?? CreateClient();
        state = ReadJson<ContentState>(statePath) ?? new();
        state.Feeds ??= new(); state.Downloads ??= new();
        foreach (var feed in state.Feeds) ValidateUrl(feed);
        foreach (var entry in state.Downloads) ValidateRecord(entry);
        state.CacheDirectory = cache;
    }

    public ContentState GetState()
    {
        lock (stateLock)
        {
            var snapshot = Clone(state);
            snapshot.Downloads = snapshot.Downloads.Where(d => File.Exists(CachePath(d.FileName))).ToList();
            return snapshot;
        }
    }

    public void SetOffline(bool offline)
    {
        lock (stateLock) { state.Offline = offline; SaveState(); }
    }

    public async Task<ContentCatalog> GetCatalogAsync(bool offline = false, CancellationToken ct = default)
    {
        await mutation.WaitAsync(ct);
        try
        {
            var result = ReadBuiltin();
            var snapshot = GetState();
            result.Offline = offline || snapshot.Offline;
            var saved = ReadJson<ContentCatalog>(catalogPath);
            if (saved != null)
                foreach (var item in saved.Items.Where(i => snapshot.Feeds.Contains(i.Source, StringComparer.Ordinal)))
                    Merge(result, ValidateItem(item, item.Source), false);
            if (!result.Offline)
            {
                foreach (var feed in snapshot.Feeds)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fetched = await ReadRemoteCatalog(feed, ct);
                        result.Items.RemoveAll(i => i.Source == feed);
                        foreach (var item in fetched.Items) Merge(result, ValidateItem(item, feed), true);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException or ArgumentException or InvalidOperationException)
                    { result.Warnings.Add($"订阅源暂不可用，保留本地列表：{new Uri(feed).Host}（{ex.Message}）"); }
                }
                ct.ThrowIfCancellationRequested();
                result.LastUpdated = DateTimeOffset.UtcNow.ToString("O");
                WriteJson(catalogPath, result);
            }
            else result.LastUpdated = saved?.LastUpdated ?? "";
            foreach (var entry in snapshot.Downloads)
            {
                var item = result.Items.FirstOrDefault(i => i.Id == entry.ItemId);
                if (item == null)
                {
                    item = new ContentItem { Id = entry.ItemId, Name = entry.Name, Version = entry.Version, SourceUrl = entry.SourceUrl, Description = "已下载的离线 MOD / 整合包", Source = "离线缓存" };
                    result.Items.Add(item);
                }
                item.Cached = true;
            }
            return result;
        }
        finally { mutation.Release(); }
    }

    public async Task AddFeedAsync(string url, CancellationToken ct = default)
    {
        var normalized = ValidateUrl(url).AbsoluteUri;
        if (GetState().Offline) throw new InvalidOperationException("请先关闭离线模式，再验证并添加订阅源。");
        var fetched = await ReadRemoteCatalog(normalized, ct);
        foreach (var item in fetched.Items) ValidateItem(item, normalized);
        ct.ThrowIfCancellationRequested();
        lock (stateLock)
        {
            if (state.Feeds.Count >= 10) throw new InvalidOperationException("最多添加 10 个社区订阅源。");
            if (!state.Feeds.Contains(normalized, StringComparer.Ordinal)) { state.Feeds.Add(normalized); SaveState(); }
        }
        await GetCatalogAsync(ct: ct);
    }

    public void RemoveFeed(string url)
    {
        var normalized = ValidateUrl(url).AbsoluteUri;
        lock (stateLock) { state.Feeds.RemoveAll(x => x == normalized); SaveState(); }
    }

    public async Task<DownloadResult> DownloadAsync(string itemId, bool install, CancellationToken ct = default)
    {
        if (!SafeId.IsMatch(itemId ?? "")) throw new ArgumentException("下载条目 ID 无效。");
        var catalog = await GetCatalogAsync(true, ct);
        var item = catalog.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new InvalidOperationException("没有找到该 MOD / 整合包，请刷新列表。");
        await mutation.WaitAsync(ct);
        try
        {
            var cached = GetState().Downloads.FirstOrDefault(i => i.ItemId == itemId);
            if (cached != null) return await Finish(cached, install, true, ct);
            EnsureOnline();
            if (item.GithubRepository.Length > 0) item = await ResolveGithub(item, ct);
            if (item.DownloadUrl.Length == 0) throw new InvalidOperationException("作者尚未提供可直连的完整 ZIP，请打开作者页面下载后导入。");
            return await DownloadItem(item, install, ct);
        }
        finally { mutation.Release(); }
    }

    public async Task<DownloadResult> DownloadUrlAsync(string url, string? sha256, bool install, CancellationToken ct = default,
        string? displayName = null, string? version = null, string? sourcePageUrl = null)
    {
        var uri = ValidateUrl(url);
        var expected = (sha256 ?? "").Trim(); ValidateHash(expected);
        var id = "url-" + Digest(Encoding.UTF8.GetBytes(uri.AbsoluteUri))[..24];
        await mutation.WaitAsync(ct);
        try
        {
            var cached = GetState().Downloads.FirstOrDefault(d => d.ItemId == id);
            if (cached != null)
            {
                if (expected.Length > 0 && !expected.Equals(cached.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("缓存 SHA-256 与填写的校验值不一致，请检查下载地址或校验值。");
                if (!string.IsNullOrWhiteSpace(displayName)) cached.Name = displayName.Trim()[..Math.Min(displayName.Trim().Length, 160)];
                if (version != null) cached.Version = version[..Math.Min(version.Length, 120)];
                if (sourcePageUrl != null) cached.SourceUrl = ValidateUrl(sourcePageUrl).GetLeftPart(UriPartial.Path);
                lock (stateLock) { Upsert(cached); SaveState(); }
                return await Finish(cached, install, true, ct);
            }
            EnsureOnline();
            var name = string.IsNullOrWhiteSpace(displayName) ? Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "社区整合包.zip").Trim('/') : displayName.Trim();
            return await DownloadItem(new() { Id = id, Name = name[..Math.Min(name.Length, 160)], Version = (version ?? "")[..Math.Min((version ?? "").Length, 120)], DownloadUrl = uri.AbsoluteUri,
                SourceUrl = sourcePageUrl == null ? uri.GetLeftPart(UriPartial.Path) : ValidateUrl(sourcePageUrl).GetLeftPart(UriPartial.Path), Sha256 = expected, Source = "自定义直链", Kind = "pack" }, install, ct);
        }
        finally { mutation.Release(); }
    }

    public async Task<DownloadResult> InstallCached(string itemId, CancellationToken ct = default)
    {
        await mutation.WaitAsync(ct);
        try { return await Finish(GetState().Downloads.FirstOrDefault(d => d.ItemId == itemId) ?? throw new InvalidOperationException("此内容尚未下载，不能离线安装。"), true, true, ct); }
        finally { mutation.Release(); }
    }

    public async Task<ContentItem> GetDetailAsync(string itemId, CancellationToken ct = default)
    {
        var catalog = await GetCatalogAsync(true, ct);
        var item = catalog.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new ArgumentException("资源已更新，请刷新列表。");
        if (GetState().Offline || item.GithubRepository.Length == 0) return item;
        try
        {
            using var response = await Send("https://api.github.com/repos/" + item.GithubRepository + "/releases/latest", ct);
            var release = JObject.Parse(await ReadText(response, ct));
            item.Version = (string?)release["tag_name"] ?? item.Version;
            item.Details = ((string?)release["body"] ?? "")[..Math.Min(((string?)release["body"] ?? "").Length, 20000)];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        { item.Details = "暂时未取得作者的更新说明，可查看下方模组信息，或在软件内打开原站。"; }
        return item;
    }

    /// <summary>Accepts browser downloads with their original login cookies; validates before cache or installation.</summary>
    public async Task<DownloadResult> CacheLocalArchiveAsync(string archivePath, string name, string sourceUrl, bool install, CancellationToken ct = default)
    {
        var source = Path.GetFullPath(archivePath); NoLinks(source);
        var info = new FileInfo(source);
        if (!info.Exists || info.Length is <= 0 or > MaxArchive) throw new InvalidDataException("下载的 Mod ZIP 无效或超过 512 MB。");
        var sourceUri = ValidateUrl(sourceUrl);
        var safeName = string.IsNullOrWhiteSpace(name) ? "社区 Mod ZIP" : Path.GetFileName(name);
        if (safeName.Length > 160) safeName = safeName[..160];
        await mutation.WaitAsync(ct);
        var temporary = Path.Combine(cache, ".browser-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBounded(input, output, info.Length, ct);
            ValidateZip(temporary);
            var digest = await DigestFile(temporary, ct);
            var record = new DownloadedContent { ItemId = "browser-" + digest[..24], Name = safeName, Sha256 = digest,
                FileName = "browser-" + digest[..24] + ".zip", Size = info.Length,
                SourceUrl = sourceUri.GetLeftPart(UriPartial.Path), DownloadedAt = DateTimeOffset.UtcNow.ToString("O") };
            var target = CachePath(record.FileName);
            if (!File.Exists(target)) File.Move(temporary, target);
            else if (!(await DigestFile(target, ct)).Equals(digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("同名缓存已损坏。");
            lock (stateLock) { Upsert(record); SaveState(); }
            return await Finish(record, install, false, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); mutation.Release(); }
    }

    public async Task ExportOfflineAsync(string destination, CancellationToken ct = default)
    {
        await mutation.WaitAsync(ct);
        var target = Path.GetFullPath(destination);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            NoLinks(target);
            if (File.Exists(target)) throw new IOException("目标文件已存在，请换一个文件名。");
            var records = GetState().Downloads;
            if (records.Count == 0) throw new InvalidOperationException("先下载 MOD 或整合包，再导出离线资料包。");
            if (records.Sum(r => r.Size) > MaxBundle) throw new InvalidDataException("离线资料包最多 1 GB，请分批导出。");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("offline-bundle.json").Open(), new UTF8Encoding(false)))
                    writer.Write(JsonConvert.SerializeObject(records));
                foreach (var record in records)
                {
                    await VerifyCached(record, ct);
                    using var input = new FileStream(CachePath(record.FileName), FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = zip.CreateEntry("Archives/" + record.FileName, CompressionLevel.NoCompression).Open();
                    await input.CopyToAsync(output, ct);
                }
            }
            ct.ThrowIfCancellationRequested(); File.Move(temp, target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); mutation.Release(); }
    }

    public async Task ImportOfflineAsync(string archivePath, CancellationToken ct = default)
    {
        await mutation.WaitAsync(ct);
        var stage = Path.Combine(cache, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.GetFullPath(archivePath); NoLinks(path);
            if (new FileInfo(path).Length > MaxBundle + MaxJson) throw new InvalidDataException("离线资料包过大，最多 1 GB。");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var metadata = zip.GetEntry("offline-bundle.json") ?? throw new InvalidDataException("不是星芽离线资料包。普通 MOD ZIP 请通过 MOD 管理导入。");
            if (metadata.Length > MaxJson) throw new InvalidDataException("离线清单过大。");
            using var reader = new StreamReader(metadata.Open());
            var records = JsonConvert.DeserializeObject<List<DownloadedContent>>(await reader.ReadToEndAsync(ct)) ?? throw new InvalidDataException("离线清单为空。");
            if (records.Count == 0 || records.Count > 250 || records.Select(r => r.ItemId).Distinct(StringComparer.Ordinal).Count() != records.Count || records.Select(r => r.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Count)
                throw new InvalidDataException("离线清单数量或条目重复。");
            long total = 0;
            foreach (var record in records) { ValidateRecord(record); total = checked(total + record.Size); }
            if (total > MaxBundle) throw new InvalidDataException("离线内容总计超过 1 GB。");
            var expected = records.Select(r => "Archives/" + r.FileName).Append("offline-bundle.json").ToHashSet(StringComparer.Ordinal);
            if (zip.Entries.Count != expected.Count || zip.Entries.Any(e => !expected.Remove(e.FullName))) throw new InvalidDataException("离线资料包包含清单以外或重复的内容。");
            Directory.CreateDirectory(stage);
            foreach (var record in records)
            {
                var entry = zip.GetEntry("Archives/" + record.FileName)!;
                if (entry.Length != record.Size || entry.Length > MaxArchive) throw new InvalidDataException("离线 ZIP 文件大小与清单不符。");
                var staged = Path.Combine(stage, record.FileName);
                using (var input = entry.Open())
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await CopyBounded(input, output, record.Size, ct);
                if (!(await DigestFile(staged, ct)).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("离线 ZIP SHA-256 校验失败：" + record.Name);
                ValidateZip(staged);
            }
            foreach (var record in records)
            {
                var target = CachePath(record.FileName);
                if (File.Exists(target))
                {
                    if (!(await DigestFile(target, ct)).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("同名缓存已被修改，请先移走损坏文件：" + record.FileName);
                }
                else File.Move(Path.Combine(stage, record.FileName), target);
            }
            lock (stateLock) { foreach (var record in records) Upsert(record); SaveState(); }
        }
        finally { if (Directory.Exists(stage)) { NoLinks(stage); Directory.Delete(stage, true); } mutation.Release(); }
    }

    private async Task<DownloadResult> DownloadItem(ContentItem item, bool install, CancellationToken ct)
    {
        ValidateItem(item, item.Source);
        var temp = Path.Combine(cache, ".download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var response = await Send(item.DownloadUrl, ct);
            var length = response.Content.Headers.ContentLength;
            if (length > MaxArchive) throw new InvalidDataException("MOD ZIP 超过 512 MB，请使用分拆后的包。");
            using (var input = await response.Content.ReadAsStreamAsync(ct))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long done = 0; var buffer = new byte[81920]; var last = Environment.TickCount64;
                while (true)
                {
                    EnsureOnline();
                    var count = await input.ReadAsync(buffer, ct); if (count == 0) break;
                    done = checked(done + count); if (done > MaxArchive) throw new InvalidDataException("下载超过 512 MB 限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    if (Environment.TickCount64 - last > 150) { Report(item, done, length); last = Environment.TickCount64; }
                }
                if (length.HasValue && done != length.Value) throw new InvalidDataException("文件下载不完整，请重试。");
                Report(item, done, length);
            }
            var digest = await DigestFile(temp, ct);
            if (item.Sha256.Length > 0 && !digest.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 校验失败，文件未保存、未安装。");
            ValidateZip(temp);
            var record = new DownloadedContent { ItemId = item.Id, Name = item.Name, Version = item.Version, FileName = item.Id + "-" + digest[..16] + ".zip", Sha256 = digest, Size = new FileInfo(temp).Length, SourceUrl = item.SourceUrl, DownloadedAt = DateTimeOffset.UtcNow.ToString("O") };
            var target = CachePath(record.FileName);
            if (File.Exists(target))
            {
                if (!(await DigestFile(target, ct)).Equals(digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("同名缓存已损坏，请先移走该文件。");
            }
            else File.Move(temp, target);
            lock (stateLock) { Upsert(record); SaveState(); }
            return await Finish(record, install, false, ct);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<DownloadResult> Finish(DownloadedContent record, bool install, bool fromCache, CancellationToken ct)
    {
        await VerifyCached(record, ct);
        var path = CachePath(record.FileName);
        var result = new DownloadResult { ItemId = record.ItemId, Name = record.Name, Path = path, Bytes = record.Size, Sha256 = record.Sha256, FromCache = fromCache };
        if (install)
        {
            ct.ThrowIfCancellationRequested(); ReportPhase(record.Name, "installing");
            result.InstallationResult = await Task.Run(() => stardew.InstallModZipAutomatically(path), ct);
            result.InstalledMods = result.InstallationResult.Mods; result.Installed = true;
            ReportPhase(record.Name, "completed");
        }
        return result;
    }

    private async Task VerifyCached(DownloadedContent record, CancellationToken ct)
    {
        ValidateRecord(record);
        var path = CachePath(record.FileName);
        if (!File.Exists(path) || new FileInfo(path).Length != record.Size || !(await DigestFile(path, ct)).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("离线缓存缺失或被修改，未安装：" + record.Name);
        ValidateZip(path);
    }

    private async Task<ContentItem> ResolveGithub(ContentItem item, CancellationToken ct)
    {
        using var response = await Send("https://api.github.com/repos/" + item.GithubRepository + "/releases/latest", ct);
        var release = JObject.Parse(await ReadText(response, ct));
        var candidates = (release["assets"] as JArray ?? new()).OfType<JObject>().Where(a => ((string?)a["name"] ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && (item.AssetPrefix.Length == 0 || ((string?)a["name"] ?? "").StartsWith(item.AssetPrefix, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count != 1) throw new InvalidOperationException("作者发布页没有唯一对应的完整 MOD ZIP，请打开来源页面手动选择。");
        var asset = candidates[0];
        item.DownloadUrl = (string?)asset["browser_download_url"] ?? "";
        item.Version = (string?)release["tag_name"] ?? "";
        if (((string?)asset["digest"] ?? "").StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) item.Sha256 = ((string)asset["digest"]!)[7..];
        return ValidateItem(item, item.Source);
    }

    private async Task<ContentCatalog> ReadRemoteCatalog(string url, CancellationToken ct)
    {
        using var response = await Send(url, ct);
        var fetched = JsonConvert.DeserializeObject<ContentCatalog>(await ReadText(response, ct)) ?? throw new InvalidDataException("订阅源不是有效的 JSON 目录。");
        if (fetched.Items == null || fetched.Items.Count > 250) throw new InvalidDataException("目录最多 250 条内容。");
        if (fetched.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != fetched.Items.Count) throw new InvalidDataException("目录 ID 重复。");
        return fetched;
    }

    private async Task<HttpResponseMessage> Send(string url, CancellationToken ct)
    {
        var current = ValidateUrl(url);
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            EnsureOnline();
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("StarSprout/0.4.3");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location == null) throw new HttpRequestException("下载重定向缺少目标地址。");
                current = ValidateUrl(new Uri(current, location).AbsoluteUri); continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("下载重定向次数过多。");
    }

    public static Uri ValidateUrl(string url)
    {
        if (url == null || url.Length > 4096 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Port != 443)
            throw new ArgumentException("请填写没有账号密码的 HTTPS 公网地址（443 端口）。");
        var host = uri.DnsSafeHost.Trim('[', ']').TrimEnd('.');
        if (uri.IsLoopback || !host.Contains('.') && !host.Contains(':') || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || new[] { ".localhost", ".local", ".internal", ".test", ".invalid", ".home", ".lan" }.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            || IPAddress.TryParse(host, out var address) && !PublicAddress(address))
            throw new ArgumentException("下载源必须是公网地址，不能访问本机或局域网。");
        return uri;
    }

    private static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] >= 64 && b[1] <= 127 || b[0] == 198 && b[1] is 18 or 19 || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xE0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(20), PooledConnectionLifetime = TimeSpan.FromMinutes(3) };
        handler.ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(a => !PublicAddress(a))) throw new HttpRequestException("下载源解析到了本机或非公网地址。");
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { socket.Dispose(); last = ex; if (ct.IsCancellationRequested) throw; }
            }
            throw new HttpRequestException("无法连接下载源。", last);
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    private static ContentItem ValidateItem(ContentItem item, string source)
    {
        if (item == null || !SafeId.IsMatch(item.Id ?? "") || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 160 || item.Description == null || item.Description.Length > 2000 || item.Author == null || item.Author.Length > 160 || item.Version == null || item.Version.Length > 120 || item.Kind is not ("mod" or "pack")) throw new InvalidDataException("目录内容字段无效。");
        item.DownloadUrl ??= ""; item.SourceUrl ??= ""; item.GithubRepository ??= ""; item.AssetPrefix ??= ""; item.Sha256 ??= ""; item.Dependencies ??= new();
        if (item.SourceUrl.Length > 0) ValidateUrl(item.SourceUrl);
        if (item.DownloadUrl.Length > 0) ValidateUrl(item.DownloadUrl);
        if (item.GithubRepository.Length > 0 && !Regex.IsMatch(item.GithubRepository, "^[a-zA-Z0-9_.-]{1,100}/[a-zA-Z0-9_.-]{1,100}$")) throw new InvalidDataException("GitHub 作者仓库无效。");
        if (item.AssetPrefix.Length > 200 || item.Dependencies.Count > 100 || item.Dependencies.Any(d => d == null || d.Length > 160)) throw new InvalidDataException("依赖清单或 ZIP 前缀无效。");
        ValidateHash(item.Sha256); item.Source = source; item.Cached = false;
        return item;
    }

    private static void Merge(ContentCatalog catalog, ContentItem item, bool reportDuplicate)
    {
        if (catalog.Items.Any(i => i.Id == item.Id)) { if (reportDuplicate) catalog.Warnings.Add("跳过与已有目录重名的社区 ID：" + item.Id); return; }
        catalog.Items.Add(item);
    }

    private static void ValidateZip(string path)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("MOD ZIP 文件数量过多。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; int manifests = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(s => s is "." or ".." || s.EndsWith(' ') || s.EndsWith('.')) || !paths.Add(name)) throw new InvalidDataException("MOD ZIP 包含不安全或重复的路径。");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("MOD ZIP 不支持符号链接。");
            if (entry.Length > 256L * 1024 * 1024 || (total = checked(total + entry.Length)) > MaxBundle) throw new InvalidDataException("MOD ZIP 解压内容超过限制。");
            if (new[] { "Stardew Valley.exe", "StardewValley.exe", "Stardew Valley.dll", "StardewValley.dll", "steam_api64.dll", "StardewModdingAPI.exe" }.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("只接受 MOD / 整合包，不接受游戏本体或 SMAPI 安装器。");
            if (!Path.GetFileName(name).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (++manifests > 250 || entry.Length > 1024 * 1024) throw new InvalidDataException("MOD 清单过多或过大。");
            using var reader = new StreamReader(entry.Open()); var manifest = JObject.Parse(reader.ReadToEnd());
            foreach (var key in new[] { "Name", "Author", "Version", "UniqueID" })
                if (manifest.GetValue(key, StringComparison.OrdinalIgnoreCase)?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)manifest.GetValue(key, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("MOD ZIP 中的清单缺少有效的 " + key);
            if (manifest.GetValue("EntryDll", StringComparison.OrdinalIgnoreCase)?.Type != JTokenType.String && manifest.GetValue("ContentPackFor", StringComparison.OrdinalIgnoreCase) is not JObject) throw new InvalidDataException("MOD 清单缺少 EntryDll 或 ContentPackFor。");
        }
        if (manifests == 0)
        {
            var files = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
            var dataExtensions = new HashSet<string>(new[] { ".json", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".txt", ".tmx", ".tsx", ".tbin", ".xnb" }, StringComparer.OrdinalIgnoreCase);
            if (files.Count == 0 || files.Any(e =>
            {
                var name = e.FullName.Replace('\\', '/');
                var parts = name.Split('/');
                return parts.Length < 2 || parts.Any(string.IsNullOrEmpty) || parts[0].StartsWith('.')
                    || !dataExtensions.Contains(Path.GetExtension(name))
                    || Path.GetFileName(name).Equals("config.json", StringComparison.OrdinalIgnoreCase);
            })) throw new InvalidDataException("ZIP 需要完整 MOD 清单，或只含 MOD 文件夹内数据的汉化补丁；不接受源码、脚本或安装程序。");
            if (zip.Entries.Any(e => (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) && e.Length != 0))
                throw new InvalidDataException("MOD ZIP 目录项包含异常数据。");
        }
    }

    private static void ValidateRecord(DownloadedContent record)
    {
        if (record == null || !SafeId.IsMatch(record.ItemId ?? "") || record.FileName == null || record.FileName != Path.GetFileName(record.FileName) || !Regex.IsMatch(record.FileName, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}\\.zip$") || !Hash.IsMatch(record.Sha256 ?? "") || record.Size <= 0 || record.Size > MaxArchive || string.IsNullOrWhiteSpace(record.Name) || record.Name.Length > 160) throw new InvalidDataException("离线缓存清单无效。");
        if (!string.IsNullOrEmpty(record.SourceUrl)) ValidateUrl(record.SourceUrl);
    }

    private static void ValidateHash(string hash) { if (hash.Length > 0 && !Hash.IsMatch(hash)) throw new ArgumentException("SHA-256 应为空或 64 位十六进制校验值。"); }
    private void EnsureOnline() { lock (stateLock) if (state.Offline) throw new InvalidOperationException("当前为离线模式：可安装已下载的缓存，不能访问网络。"); }
    private void Upsert(DownloadedContent record) { state.Downloads.RemoveAll(d => d.ItemId == record.ItemId); state.Downloads.Add(record); }
    private void SaveState() => WriteJson(statePath, state);
    private void Report(ContentItem item, long done, long? total) => Progress?.Invoke(new() { ItemId = item.Id, Name = item.Name, Bytes = done, Total = total });
    private string CachePath(string fileName) { var path = Path.GetFullPath(Path.Combine(cache, fileName)); if (!path.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("缓存路径越界。"); NoLinks(path); return path; }
    private static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!;
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task<string> DigestFile(string path, CancellationToken ct) { NoLinks(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant(); }
    private static async Task CopyBounded(Stream input, Stream output, long maximum, CancellationToken ct) { long total = 0; var buffer = new byte[81920]; int count; while ((count = await input.ReadAsync(buffer, ct)) != 0) { if ((total = checked(total + count)) > maximum) throw new InvalidDataException("内容超过声明大小。"); await output.WriteAsync(buffer.AsMemory(0, count), ct); } if (total != maximum) throw new InvalidDataException("内容不完整。"); }
    private static async Task<string> ReadText(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxJson) throw new InvalidDataException("目录数据超过 2 MB。");
        using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0) { if (output.Length + count > MaxJson) throw new InvalidDataException("目录数据超过 2 MB。"); output.Write(buffer, 0, count); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private static T? ReadJson<T>(string path) where T : class { NoLinks(path); if (!File.Exists(path)) return null; if (new FileInfo(path).Length > MaxJson) throw new InvalidDataException("本地目录数据过大。"); return JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8)); }
    private static void WriteJson(string path, object value)
    {
        NoLinks(path); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented), new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static ContentCatalog ReadBuiltin()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StarSprout.catalog.json") ?? throw new InvalidOperationException("内置 MOD 目录资源缺失。");
        using var reader = new StreamReader(stream); var result = JsonConvert.DeserializeObject<ContentCatalog>(reader.ReadToEnd()) ?? new();
        foreach (var item in result.Items) ValidateItem(item, item.Source);
        return result;
    }
    private static void NoLinks(string path)
    {
        var cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持链接或重解析点路径：" + cursor);
            cursor = Path.GetDirectoryName(cursor);
        }
    }
    public void Dispose() { if (ownsClient) client.Dispose(); mutation.Dispose(); }
}
