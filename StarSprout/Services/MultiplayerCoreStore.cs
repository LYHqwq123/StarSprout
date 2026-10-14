using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace StarSprout.Services;

/// <summary>Explicitly downloads an unmodified, pinned EasyTier core. Never executes it or installs a driver.</summary>
public sealed class MultiplayerCoreStore : IDisposable
{
    public const string Version = "2.6.4";
    public const string DownloadUrl = "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-x86_64-v2.6.4.zip";
    public const string ArchiveSha256 = "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84";
    public const long ArchiveBytes = 32_657_720;
    private const string ZipDirectory = "easytier-windows-x86_64/";
    private static readonly PinnedFile[] Files =
    {
        new("easytier-core.exe", 24_395_776, "da7eb2d24b5416f3d3407636949e964a0750e3f9dc53a828cb6799a57ead445d"),
        new("easytier-cli.exe", 9_973_248, "d8783e851e944b44a9b71b39fd02f227ec0a2a82b3165c55ead5dd32dcde53a1")
    };
    private const string Notice = "EasyTier v2.6.4 — unmodified upstream command-line core\n"
        + "Copyright: EasyTier contributors. License: GNU LGPL version 3.\n"
        + "Project: https://github.com/EasyTier/EasyTier\n"
        + "Exact corresponding source: https://github.com/EasyTier/EasyTier/archive/refs/tags/v2.6.4.zip\n"
        + "Upstream release: https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4\n"
        + "LGPL: see LICENSE-EasyTier.txt; GPL terms incorporated by LGPL: see LICENSE-GPL-3.0.txt (https://www.gnu.org/licenses/gpl-3.0.txt)\n"
        + "StarSprout starts this independent executable only after the user creates or joins a room.\n"
        + "The no-TUN mode does not install a virtual adapter, WinDivert, a system service, or a firewall rule.\n";
    private readonly string directory, installedDirectory;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim installLock = new(1, 1);
    private bool disposed;

    public string CorePath => Path.Combine(installedDirectory, "easytier-core.exe");
    public string CliPath => Path.Combine(installedDirectory, "easytier-cli.exe");
    public bool IsInstalled => GetState().Installed;

    public MultiplayerCoreStore(string dataDir, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        directory = Path.GetFullPath(dataDir);
        installedDirectory = Path.Combine(directory, "easytier-v" + Version);
        NoLinks(directory);
        Directory.CreateDirectory(directory);
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
        { Timeout = Timeout.InfiniteTimeSpan };
        ownsHttp = http == null;
    }

    /// <summary>Local integrity check only. All executable hashes are pinned in launcher source, not trusted from the manifest.</summary>
    public MultiplayerCoreState GetState()
    {
        bool installed = CheckInstalled();
        return new() { Installed = installed, CorePath = CorePath, CliPath = CliPath,
            Message = installed ? "EasyTier 联机核心已就绪。" : "联机核心尚未下载或校验未通过，请点击下载。" };
    }

    public MultiplayerCoreState VerifyInstalled()
    {
        var state = GetState();
        if (!state.Installed) throw new InvalidOperationException("联机核心不完整或校验未通过，请重新下载核心。");
        return state;
    }

    public Task<MultiplayerCoreState> InstallAsync(CancellationToken ct = default) => DownloadAsync(ct);

    public async Task<MultiplayerCoreState> DownloadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await installLock.WaitAsync(ct);
        string? archive = null, stage = null, backup = null;
        try
        {
            if (IsInstalled) return GetState();
            NoLinks(directory);
            archive = Path.Combine(directory, ".easytier-download-" + Guid.NewGuid().ToString("N") + ".zip");
            stage = Path.Combine(directory, ".easytier-stage-" + Guid.NewGuid().ToString("N"));
            backup = Path.Combine(directory, ".easytier-backup-" + Guid.NewGuid().ToString("N"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try { await DownloadArchive(archive, timeout.Token); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
            { throw new TimeoutException("联机核心下载超时，请检查网络后重试。", ex); }
            using (var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (file.Length != ArchiveBytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("联机核心安装包校验失败，请重新下载官方文件。");
            NoLinks(stage);
            Directory.CreateDirectory(stage);
            NoLinks(stage);
            await ExtractVerifiedArchive(archive, stage, ct);
            NoLinks(stage);
            File.WriteAllText(Path.Combine(stage, "LICENSE-EasyTier.txt"), MultiplayerCoreLicense.Text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "LICENSE-GPL-3.0.txt"), MultiplayerGplLicense.Text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "NOTICE.txt"), Notice, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "installed.json"), JsonConvert.SerializeObject(new InstallationManifest
            {
                Version = Version, ArchiveSha256 = ArchiveSha256, Source = DownloadUrl,
                Files = Files.Select(file => new ManifestFile { Name = file.Name, Sha256 = file.Sha256, Length = file.Length }).ToList()
            }, Formatting.Indented), new UTF8Encoding(false));
            ct.ThrowIfCancellationRequested();
            VerifyOwnedDirectory(stage);
            VerifyOwnedDirectory(installedDirectory);
            if (Directory.Exists(installedDirectory))
            {
                VerifyOwnedDirectory(backup);
                Directory.Move(installedDirectory, backup);
            }
            try { Directory.Move(stage, installedDirectory); stage = null; }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(installedDirectory)) Directory.Move(backup, installedDirectory);
                throw;
            }
            return VerifyInstalled();
        }
        finally
        {
            try
            {
                try { if (archive != null) DeleteOwnedFile(archive); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                try { if (stage != null) DeleteOwnedDirectory(stage); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                try { if (backup != null && Directory.Exists(backup) && CheckInstalled()) DeleteOwnedDirectory(backup); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            finally { installLock.Release(); }
        }
    }

    private async Task DownloadArchive(string path, CancellationToken ct)
    {
        var current = new Uri(DownloadUrl);
        for (int redirects = 0; ; redirects++)
        {
            if (!AllowedDownload(current)) throw new InvalidDataException("联机核心下载地址不受支持。");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("StarSprout-Multiplayer/1.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is { } final && !AllowedDownload(final))
                throw new InvalidDataException("联机核心下载跳转到了非官方地址。");
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects >= 5 || response.Headers.Location == null || !Uri.TryCreate(current, response.Headers.Location, out var next) || !AllowedDownload(next))
                    throw new InvalidDataException("联机核心下载跳转过多或地址不是官方来源。");
                current = next;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"官方联机核心暂时无法下载（HTTP {(int)response.StatusCode}）。");
            if (response.Content.Headers.ContentLength is { } declared && declared != ArchiveBytes)
                throw new InvalidDataException("联机核心安装包大小与官方发布记录不符。");
            NoLinks(path);
            using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            var buffer = new byte[65536];
            long total = 0;
            while (true)
            {
                int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, ArchiveBytes - total + 1)), ct);
                if (count == 0) break;
                total += count;
                if (total > ArchiveBytes) throw new InvalidDataException("联机核心安装包超过官方发布大小。");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (total != ArchiveBytes) throw new InvalidDataException("联机核心下载不完整，请重试。");
            await output.FlushAsync(ct);
            return;
        }
    }

    private static bool AllowedDownload(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.AbsoluteUri == DownloadUrl || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static async Task ExtractVerifiedArchive(string archive, string stage, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 32 || zip.Entries.Sum(entry => entry.Length) > 256L * 1024 * 1024) throw new InvalidDataException("联机核心安装包内容超出限制。");
        foreach (var entry in zip.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(segment => segment is "." or "..")
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("联机核心安装包包含不安全路径。");
        }
        foreach (var file in Files)
        {
            var entries = zip.Entries.Where(entry => entry.FullName == ZipDirectory + file.Name).ToArray();
            if (entries.Length != 1 || entries[0].Length != file.Length) throw new InvalidDataException("联机核心安装包缺少必要文件。");
            string path = Path.Combine(stage, file.Name);
            NoLinks(path);
            using (var input = entries[0].Open())
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(output, ct);
            if (!Matches(path, file)) throw new InvalidDataException("联机核心文件校验失败。");
        }
    }

    private bool CheckInstalled()
    {
        try
        {
            NoLinks(installedDirectory);
            var manifestPath = Path.Combine(installedDirectory, "installed.json");
            NoLinks(manifestPath);
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 16384) return false;
            var manifest = JsonConvert.DeserializeObject<InstallationManifest>(File.ReadAllText(manifestPath), new JsonSerializerSettings { MaxDepth = 8 });
            if (manifest?.Version != Version || manifest.ArchiveSha256 != ArchiveSha256 || manifest.Source != DownloadUrl || manifest.Files?.Count != Files.Length) return false;
            foreach (var file in Files)
            {
                var record = manifest.Files.SingleOrDefault(record => record.Name == file.Name);
                if (record?.Sha256 != file.Sha256 || record.Length != file.Length || !Matches(Path.Combine(installedDirectory, file.Name), file)) return false;
            }
            foreach (var extra in Directory.EnumerateFileSystemEntries(installedDirectory))
                if (!Files.Any(file => Path.GetFileName(extra) == file.Name) && Path.GetFileName(extra) is not ("installed.json" or "LICENSE-EasyTier.txt" or "LICENSE-GPL-3.0.txt" or "NOTICE.txt")) return false;
            foreach (var name in new[] { "LICENSE-EasyTier.txt", "LICENSE-GPL-3.0.txt", "NOTICE.txt" })
            {
                string legal = Path.Combine(installedDirectory, name);
                NoLinks(legal);
                if (!File.Exists(legal)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static bool Matches(string path, PinnedFile file)
    {
        NoLinks(path);
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Length == file.Length && Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void VerifyOwnedDirectory(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("联机核心缓存路径超出允许目录。");
        NoLinks(path);
        if (Directory.Exists(path))
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                NoLinks(child);
                if (Directory.Exists(child)) throw new IOException("联机核心目录包含非预期的子目录，请检查缓存目录。");
            }
    }

    private void DeleteOwnedDirectory(string path)
    {
        VerifyOwnedDirectory(path);
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private void DeleteOwnedFile(string path)
    {
        VerifyOwnedDirectory(path);
        if (File.Exists(path)) File.Delete(path);
    }

    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("联机核心目录不能使用符号链接或目录联接。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ownsHttp) http.Dispose();
    }

    private sealed record PinnedFile(string Name, long Length, string Sha256);
    private sealed class InstallationManifest
    {
        public string Version { get; set; } = "";
        public string ArchiveSha256 { get; set; } = "";
        public string Source { get; set; } = "";
        public List<ManifestFile> Files { get; set; } = new();
    }
    private sealed class ManifestFile
    {
        public string Name { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Length { get; set; }
    }
}
