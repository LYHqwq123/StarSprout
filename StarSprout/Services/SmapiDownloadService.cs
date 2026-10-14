using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace StarSprout.Services;

/// <summary>Downloads a pinned SMAPI installer into the launcher's cache; never runs it or changes the game.</summary>
public sealed class SmapiDownloadService : IDisposable
{
    public const string Version = "4.5.2";
    public const string OfficialSha256 = "dd01ddca7b566bfe0d3b3d2d03833496abc56c53da976241f2ab443f5484acc4";
    private const long MaxDownload = 128L * 1024 * 1024, MaxExtracted = 512L * 1024 * 1024;
    private readonly string directory;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly List<SmapiDownloadSource> sources;
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Offline { get; set; }
    public event Action<SmapiDownloadProgress>? Progress;

    public SmapiDownloadService(string dataDirectory, HttpClient? http = null)
        : this(dataDirectory, http, DefaultSources()) { }

    internal SmapiDownloadService(string dataDirectory, HttpClient? http, List<SmapiDownloadSource> sources)
    {
        directory = Path.GetFullPath(Path.Combine(dataDirectory, "Smapi", "Downloads"));
        NoLinks(directory);
        Directory.CreateDirectory(directory);
        this.sources = sources;
        foreach (var source in sources)
        {
            ValidateUrl(source.DownloadUrl);
            if (!Regex.IsMatch(source.Id, "^[a-z0-9-]{1,30}$") || !Regex.IsMatch(source.Sha256, "^[a-f0-9]{64}$"))
                throw new InvalidDataException("SMAPI 下载线路或校验信息无效。");
        }
        ownsHttp = http is null;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public SmapiDownloadState GetState() => new()
    {
        Offline = Offline, CacheDirectory = directory,
        Sources = sources.Select(x => new SmapiDownloadSource
        {
            Id = x.Id, Name = x.Name, Version = x.Version, SourceUrl = x.SourceUrl, DownloadUrl = x.DownloadUrl,
            Sha256 = x.Sha256, MinimumGameVersion = x.MinimumGameVersion, Description = x.Description
        }).ToList()
    };

    public async Task<SmapiDownloadResult> DownloadAsync(string sourceId = "domestic", CancellationToken ct = default)
    {
        var selected = sources.SingleOrDefault(x => x.Id == sourceId) ?? throw new ArgumentException("不支持的 SMAPI 下载线路。");
        await gate.WaitAsync(ct);
        try
        {
            var archive = Path.Combine(directory, "SMAPI-" + selected.Sha256 + ".zip");
            NoLinks(archive);
            var cached = File.Exists(archive) && await Digest(archive, ct) == selected.Sha256;
            var actual = selected;
            var fallback = false;
            if (!cached)
            {
                if (Offline) throw new InvalidOperationException("离线模式下尚无已校验的 SMAPI 安装包，请联网后下载。");
                try { await Fetch(selected, archive, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested && selected.Id == "domestic" && (ex is HttpRequestException or TaskCanceledException))
                {
                    actual = sources.Single(x => x.Id == "github" && x.Version == selected.Version && x.Sha256 == selected.Sha256);
                    fallback = true;
                    Progress?.Invoke(new() { Phase = "fallback", Name = "国内线路暂不可用，切换官方 GitHub" });
                    await Fetch(actual, archive, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            Progress?.Invoke(new() { Phase = "extracting", Name = "正在准备 SMAPI 安装包" });
            var result = Prepare(archive, actual, ct);
            result.FromCache = cached;
            result.UsedFallback = fallback;
            Progress?.Invoke(new() { Phase = "completed", Name = "SMAPI 安装包已准备好" });
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task Fetch(SmapiDownloadSource source, string target, CancellationToken ct)
    {
        var temporary = Path.Combine(directory, ".smapi-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var response = await Get(source.DownloadUrl, ct);
            if (response.Content.Headers.ContentLength > MaxDownload) throw new InvalidDataException("SMAPI 安装包过大，已停止下载。");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    int count;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(45));
                        try { count = await input.ReadAsync(buffer, idle.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        { throw new HttpRequestException("SMAPI 下载线路长时间未响应，请重试或切换线路。"); }
                    }
                    if (count == 0) break;
                    total += count;
                    if (total > MaxDownload) throw new InvalidDataException("SMAPI 安装包过大，已停止下载。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    Progress?.Invoke(new() { Bytes = total, Total = response.Content.Headers.ContentLength, Name = "SMAPI " + source.Version + " · " + source.Name });
                }
            }
            Progress?.Invoke(new() { Phase = "verifying", Name = "正在核对官方 SHA256" });
            if (await Digest(temporary, ct) != source.Sha256) throw new InvalidDataException("SMAPI 安装包与官方 SHA256 不一致，已拒绝该文件。请改用官方线路。");
            NoLinks(target);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<HttpResponseMessage> Get(string url, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 5; ++redirects)
        {
            ValidateUrl(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("StarSprout/0.4.0");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            try
            {
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var next = response.Headers.Location ?? throw new HttpRequestException("SMAPI 下载线路返回了无效跳转。");
                    url = new Uri(new Uri(url), next).AbsoluteUri;
                    response.Dispose();
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("SMAPI 下载失败（HTTP " + (int)response.StatusCode + "）。");
                // An injected HttpClient must not silently redirect beyond the allowlist either.
                if (response.RequestMessage?.RequestUri is { } final) ValidateUrl(final.AbsoluteUri);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("SMAPI 下载线路跳转次数过多。");
    }

    private SmapiDownloadResult Prepare(string archive, SmapiDownloadSource source, CancellationToken ct)
    {
        NoLinks(archive);
        var stage = Path.Combine(directory, "Installer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count is < 2 or > 5000) throw new InvalidDataException("SMAPI 安装包内容异常。");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(ZipArchiveEntry Entry, string Target)>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                var parts = name.TrimEnd('/').Split('/');
                if (name.Length > 1000 || name.StartsWith('/') || parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ') || Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\..*)?$", RegexOptions.IgnoreCase)))
                    throw new InvalidDataException("SMAPI 安装包包含不安全路径。");
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("SMAPI 安装包不支持符号链接。");
                if (!paths.Add(name.TrimEnd('/'))) throw new InvalidDataException("SMAPI 安装包包含重复路径。");
                var destination = Path.GetFullPath(Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SMAPI 解压路径越界。");
                if (name.EndsWith('/')) continue;
                total += entry.Length;
                if (entry.Length > MaxDownload || total > MaxExtracted) throw new InvalidDataException("SMAPI 安装包解压后过大。");
                files.Add((entry, destination));
            }
            var installers = files.Where(f => f.Entry.Name.Equals("install on Windows.bat", StringComparison.OrdinalIgnoreCase)).ToList();
            if (installers.Count != 1 || !files.Any(f => f.Entry.FullName.Replace('\\', '/').Contains("/internal/windows/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("下载内容不是支持的 SMAPI Windows 安装包。");
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
                file.Entry.ExtractToFile(file.Target, false);
            }
            var installer = installers[0].Target;
            return new()
            {
                SourceId = source.Id, SourceName = source.Name, Version = source.Version, Path = archive,
                Directory = Path.GetDirectoryName(installer)!, InstallerPath = installer, Sha256 = source.Sha256,
                Bytes = new FileInfo(archive).Length,
                Message = "已核对官方 SHA256 并解压。请打开安装包文件夹，运行 install on Windows.bat；此版本要求星露谷 1.6.14 或更新版本。"
            };
        }
        catch
        {
            NoLinks(stage);
            Directory.Delete(stage, true);
            throw;
        }
    }

    private static List<SmapiDownloadSource> DefaultSources() => new()
    {
        new()
        {
            Id = "domestic", Name = "国内 · 3DM", Version = Version, Sha256 = OfficialSha256,
            SourceUrl = "https://mod.3dmgame.com/mod/255905",
            DownloadUrl = "https://dmod.3dmgame.com/static/upload/resource2/202607/_20260708111457_68634.zip",
            Description = "3DM 社区公开转载线路；下载后必须与同版本官方 SHA256 完全一致，失败可使用官方线路。"
        },
        new()
        {
            Id = "github", Name = "官方 · GitHub", Version = Version, Sha256 = OfficialSha256,
            SourceUrl = "https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2",
            DownloadUrl = "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip",
            Description = "Pathoschild 官方发布；SHA256 来自官方 GitHub Release 的 digest。"
        }
    };

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("SMAPI 下载地址无效。");
        var host = uri.IdnHost.ToLowerInvariant();
        var allowed = host == "dmod.3dmgame.com" && uri.AbsolutePath.StartsWith("/static/upload/resource", StringComparison.Ordinal) ||
                      host == "github.com" && uri.AbsolutePath.StartsWith("/Pathoschild/SMAPI/releases/download/", StringComparison.Ordinal) ||
                      host == "release-assets.githubusercontent.com" || host == "objects.githubusercontent.com";
        if (!allowed) throw new InvalidDataException("SMAPI 下载被引向未知站点，已停止。");
    }

    private static async Task<string> Digest(string path, CancellationToken ct)
    {
        NoLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static void NoLinks(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("SMAPI 缓存不支持链接或重解析点路径。");
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); gate.Dispose(); }
}
