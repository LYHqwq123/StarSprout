using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace StarSprout.Services;

/// <summary>Stores appearance settings and a decoded PNG, without exposing user file paths to the page.</summary>
public sealed class AppearanceService
{
    private const long MaxImageBytes = 20L * 1024 * 1024;
    private const long MaxPixels = 40_000_000;
    private const int MaxDimension = 12_000;
    private const int StoredDimension = 3_840;
    private static readonly Regex Color = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
    private readonly string directory, settingsPath, backgroundPath;
    private readonly object sync = new();
    private StoredSettings settings;

    public string AssetsDirectory { get; }

    public AppearanceService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        directory = Path.GetFullPath(dataDirectory);
        AssetsDirectory = Path.Combine(directory, "assets");
        settingsPath = Path.Combine(directory, "appearance-settings.json");
        backgroundPath = Path.Combine(AssetsDirectory, "background.png");
        NoLinks(AssetsDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        settings = LoadSettings();
        if (settings.BackgroundName.Length > 0 && !ValidStoredBackground())
        {
            settings.BackgroundName = "";
            settings.Revision = "";
            WriteSettings(settings);
        }
    }

    public AppearanceSettingsView GetSettings()
    {
        lock (sync) return View();
    }

    public AppearanceSettingsView SaveSettings(AppearanceSettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        lock (sync)
        {
            var next = Copy(input);
            next.BackgroundName = settings.BackgroundName;
            next.Revision = settings.Revision;
            WriteSettings(next);
            settings = next;
            return View();
        }
    }

    public AppearanceSettingsView ImportBackground(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var fullPath = Path.GetFullPath(localPath);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("请选择电脑上的本地图像。");
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp")
            throw new ArgumentException("背景图片支持 PNG、JPEG 和 BMP。");
        NoLinks(fullPath);
        byte[] source;
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length is <= 0 or > MaxImageBytes)
                throw new ArgumentException("背景图片不能超过 20 MiB。");
            source = new byte[checked((int)stream.Length)];
            stream.ReadExactly(source);
        }
        var png = EncodeBackground(source);
        var displayName = Path.GetFileName(fullPath);
        displayName = new string(displayName.Where(c => !char.IsControl(c)).Take(120).ToArray());
        var revision = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        lock (sync)
        {
            NoLinks(backgroundPath);
            AtomicWrite(backgroundPath, png);
            var next = Copy(settings);
            next.BackgroundName = displayName;
            next.Revision = revision;
            WriteSettings(next);
            settings = next;
            return View();
        }
    }

    public AppearanceSettingsView ClearBackground()
    {
        lock (sync)
        {
            NoLinks(backgroundPath);
            var next = Copy(settings);
            next.BackgroundName = "";
            next.Revision = "";
            WriteSettings(next);
            settings = next;
            if (File.Exists(backgroundPath)) File.Delete(backgroundPath);
            return View();
        }
    }

    private AppearanceSettingsView View()
    {
        NoLinks(backgroundPath);
        bool present = settings.BackgroundName.Length > 0 && File.Exists(backgroundPath);
        return new()
        {
            Theme = settings.Theme, Accent = settings.Accent, Blur = settings.Blur,
            Opacity = settings.Opacity, Fit = settings.Fit, Overlay = settings.Overlay,
            PanelOpacity = settings.PanelOpacity, HasBackground = present,
            BackgroundName = present ? settings.BackgroundName : "",
            BackgroundUrl = present ? "https://appearance.starsprout.local/background.png?v=" + settings.Revision : ""
        };
    }

    private StoredSettings LoadSettings()
    {
        NoLinks(settingsPath);
        if (!File.Exists(settingsPath)) return new();
        try
        {
            using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64 * 1024) return new();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var loaded = JsonConvert.DeserializeObject<StoredSettings>(reader.ReadToEnd()) ?? new();
            Validate(loaded);
            loaded.BackgroundName ??= "";
            loaded.Revision ??= "";
            if (loaded.BackgroundName.Length > 120 || loaded.BackgroundName.Any(char.IsControl)
                || loaded.BackgroundName.IndexOfAny(new[] { '/', '\\' }) >= 0)
                return new();
            if (loaded.Revision.Length > 0 && !Regex.IsMatch(loaded.Revision, "^[a-f0-9]{64}$"))
                return new();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            return new();
        }
    }

    private bool ValidStoredBackground()
    {
        NoLinks(backgroundPath);
        if (!File.Exists(backgroundPath)) return false;
        try
        {
            using var stream = new FileStream(backgroundPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxImageBytes) return false;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder is not PngBitmapDecoder || decoder.Frames.Count != 1) return false;
            var frame = decoder.Frames[0];
            ValidateDimensions(frame);
            if (frame.PixelWidth > StoredDimension || frame.PixelHeight > StoredDimension) return false;
            // Read every row so a truncated image cannot retain an apparently valid header.
            int stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
            var row = new byte[stride];
            for (int y = 0; y < frame.PixelHeight; y++)
                frame.CopyPixels(new System.Windows.Int32Rect(0, y, frame.PixelWidth, 1), row, stride, 0);
            return true;
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static byte[] EncodeBackground(byte[] source)
    {
        try
        {
            using var input = new MemoryStream(source, false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand);
            if (decoder is not PngBitmapDecoder and not JpegBitmapDecoder and not BmpBitmapDecoder || decoder.Frames.Count != 1)
                throw new ArgumentException("请选择单张 PNG、JPEG 或 BMP 图像。");
            var frame = decoder.Frames[0];
            ValidateDimensions(frame);
            double scale = Math.Min(1, (double)StoredDimension / Math.Max(frame.PixelWidth, frame.PixelHeight));
            // High entropy JPEGs can grow when converted to PNG; reduce dimensions until stored data stays bounded.
            for (int attempt = 0; attempt < 8; attempt++, scale *= 0.75)
            {
                BitmapSource image = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = new MemoryStream();
                encoder.Save(output);
                if (output.Length <= MaxImageBytes) return output.ToArray();
            }
            throw new ArgumentException("图像转换后过大，请先缩小图片。");
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new ArgumentException("图片无法解码，可能已损坏或格式不受支持。");
        }
    }

    private static void ValidateDimensions(BitmapSource frame)
    {
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > MaxDimension
            || frame.PixelHeight > MaxDimension || (long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            throw new ArgumentException("图片尺寸不能超过 12000 × 12000，且总像素不能超过 4000 万。");
    }

    private static void Validate(AppearanceSettingsInput input)
    {
        if (input.Theme is not "light" and not "dark" and not "oled" and not "system")
            throw new ArgumentException("请选择浅色、深色、OLED 或跟随系统主题。");
        if (input.Accent == null || !Color.IsMatch(input.Accent))
            throw new ArgumentException("强调色需要使用 #RRGGBB 格式。");
        if (input.Blur is < 0 or > 40 || input.Opacity is < 10 or > 100 || input.PanelOpacity is < 50 or > 100)
            throw new ArgumentException("背景模糊范围为 0–40，背景可见度为 10–100，面板不透明度为 50–100。");
        if (input.Fit is not "cover" and not "contain" and not "stretch" and not "tile")
            throw new ArgumentException("请选择支持的背景适应方式。");
    }

    private static StoredSettings Copy(AppearanceSettingsInput input) => new()
    {
        Theme = input.Theme, Accent = input.Accent.ToLowerInvariant(), Blur = input.Blur,
        Opacity = input.Opacity, Fit = input.Fit, Overlay = input.Overlay, PanelOpacity = input.PanelOpacity
    };

    private void WriteSettings(StoredSettings value) => AtomicWrite(settingsPath,
        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented)));

    private static void AtomicWrite(string path, byte[] bytes)
    {
        NoLinks(path);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            NoLinks(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void NoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("主题文件或目录不能是符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private sealed class StoredSettings : AppearanceSettingsInput
    {
        [JsonProperty("backgroundName")] public string BackgroundName { get; set; } = "";
        [JsonProperty("revision")] public string Revision { get; set; } = "";
    }
}
