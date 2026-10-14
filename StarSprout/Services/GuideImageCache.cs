using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace StarSprout.Services;

/// <summary>Fetches only requested BWIKI article images; cached images remain available offline.</summary>
public sealed class GuideImageCache : IDisposable
{
    private const string ImageHost = "patchwiki.biligame.com";
    private const string ImagePath = "/images/stardewvalley/";
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const long MaxCacheBytes = 200L * 1024 * 1024;
    private readonly string directory;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly object cacheSync = new();
    private readonly SemaphoreSlim requests = new(3, 3);
    private volatile bool offline;
    private bool disposed;

    public bool Offline { get => offline; set => offline = value; }

    public GuideImageCache(string dataDir, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        directory = Path.Combine(Path.GetFullPath(dataDir), "guide-images");
        NoLinks(directory);
        Directory.CreateDirectory(directory);
        this.http = http ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };
        ownsHttp = http == null;
        lock (cacheSync) MakeRoom(0, null);
    }

    public static bool IsAllowedUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || value.Contains('\\') || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        // Reject malformed and nested escapes rather than relying on differing URL normalizers.
        for (int index = 0; index < value.Length; index++)
            if (value[index] == '%' && (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2])))
                return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(ImageHost, StringComparison.OrdinalIgnoreCase) || uri.Port != 443
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
            || !uri.AbsolutePath.StartsWith(ImagePath, StringComparison.Ordinal)
            || uri.AbsolutePath.Length <= ImagePath.Length) return false;
        var decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!decodedPath.StartsWith(ImagePath, StringComparison.Ordinal) || decodedPath.Contains('\\')
            || decodedPath.Contains('%') || decodedPath.Any(char.IsControl)) return false;
        return !decodedPath.Split('/').Any(segment => segment is "." or "..");
    }

    public async Task<GuideImage> GetAsync(string url, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        if (!IsAllowedUrl(url)) throw new ArgumentException("只允许读取星露谷 BWIKI 的公开攻略图片。", nameof(url));
        var normalized = new Uri(url).AbsoluteUri;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant() + ".img";
        var path = Path.Combine(directory, key);
        if (TryRead(path) is { } cached) return cached;
        ThrowIfOffline();
        await requests.WaitAsync(ct);
        try
        {
            if (TryRead(path) is { } queuedCache) return queuedCache;
            ThrowIfOffline();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            GuideImage image;
            try { image = await FetchAsync(new Uri(normalized), timeout.Token); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException("攻略图片加载超时，请稍后重试。", ex);
            }
            TryStore(path, image.Bytes);
            return image;
        }
        finally { requests.Release(); }
    }

    private async Task<GuideImage> FetchAsync(Uri current, CancellationToken ct)
    {
        for (int redirects = 0; ; redirects++)
        {
            ThrowIfOffline();
            if (!IsAllowedUrl(current.AbsoluteUri)) throw new InvalidDataException("攻略图片跳转到了不支持的地址。");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("StarSprout-GuideReader/1.0");
            request.Headers.Accept.ParseAdd("image/png,image/jpeg,image/gif,image/webp");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // An injected client must also have automatic redirects disabled; never consume an unrelated final response.
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsAllowedUrl(finalUri.AbsoluteUri))
                throw new InvalidDataException("攻略图片跳转到了不支持的地址。");
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects >= 3 || response.Headers.Location == null
                    || !Uri.TryCreate(current, response.Headers.Location, out var next) || !IsAllowedUrl(next.AbsoluteUri))
                    throw new InvalidDataException("攻略图片跳转过多或地址不受支持。");
                current = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"攻略图片暂时无法加载（HTTP {(int)response.StatusCode}）。", null, response.StatusCode);
            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > MaxImageBytes) throw TooLarge();
            string? declaredType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (declaredType is not null and not "application/octet-stream" and not "binary/octet-stream"
                and not "image/png" and not "image/jpeg" and not "image/jpg" and not "image/pjpeg" and not "image/gif" and not "image/webp")
                throw new InvalidDataException("攻略图片返回了不支持的格式。");
            using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream(declaredLength is > 0 ? (int)declaredLength.Value : 0);
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int remaining = MaxImageBytes - checked((int)output.Length);
                    int count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), ct);
                    if (count == 0) break;
                    if (count > remaining) throw TooLarge();
                    output.Write(buffer, 0, count);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            var bytes = output.ToArray();
            var contentType = DetectType(bytes) ?? throw new InvalidDataException("攻略图片已损坏或不支持此图片格式。");
            if (declaredType is not null && declaredType.StartsWith("image/", StringComparison.Ordinal)
                && NormalizeType(declaredType) != contentType)
                throw new InvalidDataException("攻略图片格式与服务器声明不一致。");
            return new GuideImage { Bytes = bytes, ContentType = contentType };
        }
    }

    private GuideImage? TryRead(string path)
    {
        lock (cacheSync)
        {
            NoLinks(path);
            if (!File.Exists(path)) return null;
            try
            {
                byte[] bytes;
                bool invalidLength;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    invalidLength = stream.Length is <= 0 or > MaxImageBytes;
                    bytes = invalidLength ? Array.Empty<byte>() : new byte[(int)stream.Length];
                    if (!invalidLength) stream.ReadExactly(bytes);
                }
                if (invalidLength) return RemoveInvalid(path);
                var contentType = DetectType(bytes);
                if (contentType == null) return RemoveInvalid(path);
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return new GuideImage { Bytes = bytes, ContentType = contentType };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static GuideImage? RemoveInvalid(string path)
    {
        try { NoLinks(path); File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private void TryStore(string path, byte[] bytes)
    {
        lock (cacheSync)
        {
            NoLinks(directory);
            var temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(directory);
                NoLinks(path);
                if (!MakeRoom(bytes.Length, path)) return;
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes);
                    output.Flush(true);
                }
                NoLinks(path);
                File.Move(temp, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full/read-only cache must not hide an otherwise valid downloaded picture.
            }
            finally
            {
                try { NoLinks(temp); if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private bool MakeRoom(int incomingBytes, string? replacePath)
    {
        NoLinks(directory);
        var candidates = new List<FileInfo>();
        long total = incomingBytes;
        foreach (var path in Directory.EnumerateFiles(directory, "*.img", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 68 || name[..64].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) continue;
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || string.Equals(path, replacePath, StringComparison.OrdinalIgnoreCase)) continue;
            total += file.Length;
            candidates.Add(file);
        }
        foreach (var file in candidates.OrderBy(file => file.LastAccessTimeUtc))
        {
            if (total <= MaxCacheBytes) break;
            try
            {
                NoLinks(file.FullName);
                long length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total <= MaxCacheBytes;
    }

    private static string? DetectType(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length >= 33 && span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadUInt32BigEndian(span[8..12]) == 13 && span[12..16].SequenceEqual("IHDR"u8)
            && ValidDimensions(BinaryPrimitives.ReadUInt32BigEndian(span[16..20]), BinaryPrimitives.ReadUInt32BigEndian(span[20..24])))
            return "image/png";
        if (span.Length >= 4 && span[0] == 255 && span[1] == 216 && span[2] == 255) return "image/jpeg";
        if (span.Length >= 13 && (span[..6].SequenceEqual("GIF87a"u8) || span[..6].SequenceEqual("GIF89a"u8))
            && ValidDimensions(BinaryPrimitives.ReadUInt16LittleEndian(span[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(span[8..10])))
            return "image/gif";
        if (span.Length >= 20 && span[..4].SequenceEqual("RIFF"u8) && span[8..12].SequenceEqual("WEBP"u8)
            && BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]) == span.Length - 8
            && (span[12..16].SequenceEqual("VP8 "u8) || span[12..16].SequenceEqual("VP8L"u8) || span[12..16].SequenceEqual("VP8X"u8)))
            return "image/webp";
        return null;
    }

    private static bool ValidDimensions(uint width, uint height) => width is > 0 and <= 32768
        && height is > 0 and <= 32768 && (long)width * height <= 80_000_000;

    private static string NormalizeType(string value) => value is "image/jpg" or "image/pjpeg" ? "image/jpeg" : value;
    private static InvalidDataException TooLarge() => new("攻略图片不能超过 10 MiB。");
    private void ThrowIfOffline()
    {
        if (Offline) throw new InvalidOperationException("离线模式下这张图片尚未缓存；联网打开一次后即可离线查看。");
    }

    private static void NoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("攻略图片缓存不能使用符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ownsHttp) http.Dispose();
    }
}

public sealed class GuideImage
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/octet-stream";
}
