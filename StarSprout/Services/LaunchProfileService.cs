using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace StarSprout.Services;

/// <summary>Remembers the selected game platform, leaving account login to its official client.</summary>
public sealed class LaunchProfileService
{
    private readonly string path;
    private readonly string defaultPlatform;
    private readonly Func<string, bool> protocolAvailable;
    private readonly object sync = new();
    private string platform;

    public LaunchProfileService(string dataDir, string defaultPlatform = "offline", Func<string, bool>? protocolAvailable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        this.defaultPlatform = Normalize(defaultPlatform);
        this.protocolAvailable = protocolAvailable ?? IsProtocolRegistered;
        var directory = Path.GetFullPath(dataDir);
        path = Path.Combine(directory, "launch-profile.json");
        NoLinks(path);
        Directory.CreateDirectory(directory);
        platform = Load();
    }

    public LaunchProfileState GetState()
    {
        string selected;
        lock (sync) selected = platform;
        return new LaunchProfileState
        {
            Platform = selected,
            DisplayName = selected switch { "steam" => "Steam 当前账号", "xbox" => "Xbox 当前账号", _ => "离线玩家" },
            Description = selected switch
            {
                "steam" => "使用 Steam 客户端当前登录的账号；登录或切换账号请在 Steam 客户端中完成。",
                "xbox" => "使用 Xbox 客户端当前登录的账号；需要已安装的 PC 版游戏及有效许可。",
                _ => "启动已配置的本地独立版游戏目录；离线模式暂停联网下载与攻略查询，可读取已有缓存。"
            },
            CanOpenClient = ResolveClientUri(selected, protocolAvailable) != null
        };
    }

    public IReadOnlyList<LaunchProfileDescriptor> GetProfiles() => new[]
    {
        new LaunchProfileDescriptor { Id = "steam", Name = "Steam", Description = "Steam 客户端当前账号" },
        new LaunchProfileDescriptor { Id = "xbox", Name = "Xbox / PC Game Pass", Description = "Xbox 客户端当前账号" },
        new LaunchProfileDescriptor { Id = "offline", Name = "离线游玩", Description = "已配置的本地独立版游戏" }
    };

    public LaunchProfileState SetPlatform(string value)
    {
        string next = Normalize(value);
        lock (sync)
        {
            NoLinks(path);
            var temp = Path.Combine(Path.GetDirectoryName(path)!, ".launch-profile-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new StoredSelection { Platform = next }, Formatting.Indented));
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes);
                    file.Flush(true);
                }
                NoLinks(path);
                File.Move(temp, path, true);
                platform = next;
            }
            finally
            {
                NoLinks(temp);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        return GetState();
    }

    /// <summary>Creates a fixed URI launch plan; this method never starts a process or handles login secrets.</summary>
    public static ProcessStartInfo GetClientStartInfo(string platform, Func<string, bool>? protocolAvailable = null)
    {
        platform = Normalize(platform);
        var uri = ResolveClientUri(platform, protocolAvailable ?? IsProtocolRegistered);
        if (uri == null)
            throw new InvalidOperationException(platform switch
            {
                "steam" => "未检测到 Steam 客户端，请先安装并在官方客户端登录。",
                "xbox" => "未检测到 Xbox 客户端，请先安装并在官方客户端登录。",
                _ => "离线游玩不需要平台客户端。"
            });
        return new ProcessStartInfo(uri) { UseShellExecute = true };
    }

    /// <summary>Steam handles ownership, login, and its configured launch options. Observe the game process separately.</summary>
    public static ProcessStartInfo GetSteamGameStartInfo() => new("steam://rungameid/413150") { UseShellExecute = true };

    private static string? ResolveClientUri(string platform, Func<string, bool> available)
    {
        if (platform == "steam") return available("steam") ? "steam://open/main" : null;
        if (platform != "xbox") return null;
        // Verified in Microsoft's GamingApp manifest; legacy "ms-xbox" is not used by the current PC app.
        foreach (var protocol in new[] { "xbox", "msxbox", "msgamingapp" })
            if (available(protocol)) return protocol + "://";
        return null;
    }

    private string Load()
    {
        NoLinks(path);
        if (!File.Exists(path)) return defaultPlatform;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 8192) return defaultPlatform;
            using var reader = new StreamReader(file, Encoding.UTF8, true);
            var stored = JsonConvert.DeserializeObject<StoredSelection>(reader.ReadToEnd(), new JsonSerializerSettings { MaxDepth = 8 });
            return Normalize(stored?.Platform);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return defaultPlatform;
        }
    }

    private static string Normalize(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        return normalized is "steam" or "xbox" or "offline" ? normalized
            : throw new ArgumentException("请选择 Steam、Xbox 或离线游玩。", nameof(value));
    }

    private static bool IsProtocolRegistered(string protocol)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(protocol, false);
            return key?.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return false; }
    }

    private static void NoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("启动账号设置不能使用符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private sealed class StoredSelection
    {
        [JsonProperty("platform")] public string Platform { get; set; } = "";
    }
}
