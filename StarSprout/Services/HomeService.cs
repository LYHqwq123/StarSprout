using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace StarSprout.Services;

/// <summary>Local play dates and independent, decoded homepage artwork.</summary>
public sealed class HomeService
{
    private const int MaxDays = 3_660;
    private const int MaxLaunchesPerDay = 1_000_000;
    private const int MaxHistoryBytes = 1_048_576;
    private static readonly Regex Color = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
    private readonly string historyPath;
    private readonly Func<DateTimeOffset> localNow;
    private readonly AppearanceService imageStore;
    private readonly object sync = new();
    private List<HomePlayDay> history;

    // This directory contains only the homepage's decoded PNG and can be mapped to the fixed home host.
    public string AssetsDirectory => imageStore.AssetsDirectory;

    public HomeService(string dataDirectory, Func<DateTimeOffset>? localNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        NoLinks(directory);
        Directory.CreateDirectory(directory);
        historyPath = Path.Combine(directory, "play-calendar.json");
        this.localNow = localNow ?? (() => DateTimeOffset.Now);
        imageStore = new AppearanceService(Path.Combine(directory, "home-media"));
        history = LoadHistory();
    }

    public HomeState GetState(int? year = null, int? month = null)
    {
        var today = LocalDate();
        int selectedYear = year ?? today.Year, selectedMonth = month ?? today.Month;
        _ = new DateOnly(selectedYear, selectedMonth, 1);
        lock (sync) return View(selectedYear, selectedMonth, today);
    }

    /// <summary>Call only after the game process was successfully started, for either launch mode.</summary>
    public HomeState RecordSuccessfulLaunch(string accent)
    {
        if (accent == null || !Color.IsMatch(accent))
            throw new ArgumentException("日历主题颜色需要使用 #RRGGBB 格式。", nameof(accent));
        var today = LocalDate();
        var date = Format(today);
        lock (sync)
        {
            var next = history.Select(Copy).ToList();
            var day = next.FirstOrDefault(item => item.Date == date);
            if (day == null)
                next.Add(new() { Date = date, Accent = accent.ToLowerInvariant(), Count = 1 });
            else
            {
                day.Accent = accent.ToLowerInvariant();
                day.Count = Math.Min(MaxLaunchesPerDay, day.Count + 1);
            }
            next = next.OrderBy(item => item.Date, StringComparer.Ordinal).TakeLast(MaxDays).ToList();
            WriteHistory(next);
            history = next;
            return View(today.Year, today.Month, today);
        }
    }

    public HomeState ImportImage(string localPath)
    {
        // Reuse the global background's bounded PNG/JPEG/BMP decoder in an independent asset store.
        // It copies and re-encodes the image and never modifies the user's selected file.
        lock (sync)
        {
            imageStore.ImportBackground(localPath);
            var today = LocalDate();
            return View(today.Year, today.Month, today);
        }
    }

    public HomeState ClearImage()
    {
        lock (sync)
        {
            imageStore.ClearBackground();
            var today = LocalDate();
            return View(today.Year, today.Month, today);
        }
    }

    private HomeState View(int year, int month, DateOnly today)
    {
        var prefix = $"{year:D4}-{month:D2}-";
        var image = imageStore.GetSettings();
        return new()
        {
            Year = year, Month = month, Today = Format(today),
            Days = history.Where(day => day.Date.StartsWith(prefix, StringComparison.Ordinal)).Select(Copy).ToList(),
            HasImage = image.HasBackground, ImageName = image.BackgroundName,
            ImageUrl = image.HasBackground
                ? image.BackgroundUrl.Replace("https://appearance.starsprout.local/", "https://home.starsprout.local/", StringComparison.Ordinal)
                : ""
        };
    }

    private List<HomePlayDay> LoadHistory()
    {
        NoLinks(historyPath);
        if (!File.Exists(historyPath)) return new();
        try
        {
            using var stream = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxHistoryBytes) return new();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var loaded = JsonConvert.DeserializeObject<StoredHistory>(reader.ReadToEnd());
            return (loaded?.Days ?? new())
                .Where(day => day != null && DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _) && day.Accent != null && Color.IsMatch(day.Accent)
                    && day.Count is > 0 and <= MaxLaunchesPerDay)
                .GroupBy(day => day.Date, StringComparer.Ordinal)
                .Select(group => Copy(group.Last()))
                .OrderBy(day => day.Date, StringComparer.Ordinal).TakeLast(MaxDays).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            return new();
        }
    }

    private void WriteHistory(List<HomePlayDay> days)
    {
        NoLinks(historyPath);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new StoredHistory { Days = days }, Formatting.Indented));
        if (bytes.Length > MaxHistoryBytes) throw new IOException("启动日历超过保存上限。");
        var temp = Path.Combine(Path.GetDirectoryName(historyPath)!, ".play-calendar-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            NoLinks(historyPath);
            File.Move(temp, historyPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private DateOnly LocalDate() => DateOnly.FromDateTime(localNow().DateTime);
    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static HomePlayDay Copy(HomePlayDay day) => new()
        { Date = day.Date, Accent = day.Accent.ToLowerInvariant(), Count = day.Count };

    private static void NoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("首页文件或目录不能是符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private sealed class StoredHistory
    {
        [JsonProperty("days")] public List<HomePlayDay> Days { get; set; } = new();
    }
}
