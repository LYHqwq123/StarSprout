using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace StarSprout.Services;

/// <summary>
/// Local, process-observed playtime. The host checkpoints a verified game every 15 seconds.
/// Restored sessions remain unverified until the host checks PID plus process start time.
/// </summary>
public sealed class PlaytimeService
{
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const long MaxTotalTicks = 36525L * TimeSpan.TicksPerDay;
    private readonly string statePath;
    private readonly Func<DateTimeOffset> now;
    private readonly TimeZoneInfo localTimeZone;
    private readonly object sync = new();
    private Stored stored;
    private bool confirmedAlive;

    public PlaytimeService(string dataDirectory, Func<DateTimeOffset>? now = null, TimeZoneInfo? localTimeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        NoLinks(directory);
        Directory.CreateDirectory(directory);
        statePath = Path.Combine(directory, "playtime.json");
        this.now = now ?? (() => DateTimeOffset.Now);
        // A supplied clock's offset is a convenient deterministic zone for tests. Real sessions use the OS zone.
        this.localTimeZone = localTimeZone ?? (now == null ? TimeZoneInfo.Local : TimeZoneInfo.CreateCustomTimeZone("PlaytimeClock", this.now().Offset, "PlaytimeClock", "PlaytimeClock"));
        stored = Load();
    }

    public PlaytimeActiveSession? ActiveSession
    {
        get
        {
            lock (sync) return stored.Active == null ? null : new()
            {
                Pid = stored.Active.Pid, ProcessStartUtc = stored.Active.ProcessStartUtc,
                GameDirectory = stored.Active.GameDirectory, Platform = stored.Active.Platform
            };
        }
    }

    public PlaytimeState BeginSession(string gameDirectory, string platform, int pid, DateTimeOffset processStartUtc)
    {
        string directory = NormalizeGameDirectory(gameDirectory);
        platform = NormalizePlatform(platform);
        if (pid <= 0 || processStartUtc == DateTimeOffset.MinValue) throw new ArgumentException("游戏进程信息无效。");
        var instant = now();
        processStartUtc = processStartUtc.ToUniversalTime();
        lock (sync)
        {
            if (stored.Active != null && stored.Active.Pid == pid && stored.Active.ProcessStartUtc == processStartUtc
                && string.Equals(stored.Active.GameDirectory, directory, StringComparison.OrdinalIgnoreCase) && stored.Active.Platform == platform)
                return Checkpoint(true);
            if (stored.Active != null) throw new InvalidOperationException("请先结束或核实上一段游戏计时，再开始新的会话。");
            var next = Copy(stored);
            // Never infer historical playtime from a process that predates observation.
            next.Active = new() { Pid = pid, ProcessStartUtc = processStartUtc, GameDirectory = directory, Platform = platform, LastCheckpointUtc = instant.ToUniversalTime() };
            Save(next); stored = next; confirmedAlive = true;
            return View(stored, instant, true);
        }
    }

    public PlaytimeState Checkpoint(bool processAlive)
    {
        var instant = now();
        lock (sync)
        {
            if (stored.Active == null) return View(stored, instant, false);
            var next = Copy(stored);
            if (processAlive) Advance(next, instant);
            else Finish(next);
            Save(next); stored = next; confirmedAlive = processAlive;
            return View(stored, instant, confirmedAlive);
        }
    }

    /// <summary>Unknown/dead/reused process: keep only time confirmed through the previous checkpoint.</summary>
    public PlaytimeState EndSession() => Checkpoint(false);

    /// <summary>Use only an exit time obtained from the verified game's Process object.</summary>
    public PlaytimeState FinishSession(DateTimeOffset processExitUtc)
    {
        var instant = now();
        lock (sync)
        {
            if (stored.Active == null) return View(stored, instant, false);
            var next = Copy(stored);
            // A future exit timestamp or a backwards clock must never add fabricated time.
            Advance(next, processExitUtc <= instant ? processExitUtc : instant);
            Finish(next);
            Save(next); stored = next; confirmedAlive = false;
            return View(stored, instant, false);
        }
    }

    public PlaytimeState GetState()
    {
        var instant = now();
        lock (sync)
        {
            // Snapshot interpolation is allowed only after this launcher instance has verified identity/aliveness.
            var view = confirmedAlive && stored.Active != null ? Copy(stored) : stored;
            if (confirmedAlive) Advance(view, instant);
            return View(view, instant, confirmedAlive);
        }
    }

    private void Advance(Stored state, DateTimeOffset until)
    {
        if (state.Active == null) return;
        long cursor = state.Active.LastCheckpointUtc.UtcTicks;
        long end = until.UtcTicks;
        if (end <= cursor) return; // Retain the previous high-water mark until the clock catches up.
        long elapsed = Math.Min(end - cursor, MaxTotalTicks - state.TotalTicks);
        long countedEnd = cursor + elapsed;
        while (cursor < countedEnd)
        {
            var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(cursor, TimeSpan.Zero), localTimeZone);
            string date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            long boundary = NextMidnightUtc(local.Date);
            long sliceEnd = Math.Min(countedEnd, boundary);
            if (sliceEnd <= cursor) throw new InvalidDataException("本地时区的日期边界无效。");
            long slice = sliceEnd - cursor;
            state.Days[date] = checked(state.Days.GetValueOrDefault(date) + slice);
            cursor = sliceEnd;
        }
        state.TotalTicks += elapsed;
        state.Active.CountedTicks = Math.Min(MaxTotalTicks, state.Active.CountedTicks + elapsed);
        state.Active.LastCheckpointUtc = until.ToUniversalTime();
    }

    private long NextMidnightUtc(DateTime localDate)
    {
        var next = DateTime.SpecifyKind(localDate.AddDays(1), DateTimeKind.Unspecified);
        // A few time zones historically moved the clock at midnight or skipped an entire date.
        for (int minutes = 0; localTimeZone.IsInvalidTime(next); minutes++)
        {
            if (minutes > 2880) throw new InvalidDataException("无法计算本地日期边界。");
            next = next.AddMinutes(1);
        }
        var offset = localTimeZone.IsAmbiguousTime(next)
            ? localTimeZone.GetAmbiguousTimeOffsets(next).Max()
            : localTimeZone.GetUtcOffset(next);
        return new DateTimeOffset(next, offset).UtcTicks;
    }

    private static void Finish(Stored state)
    {
        if (state.Active == null) return;
        state.LastSessionTicks = state.Active.CountedTicks;
        state.LastPlatform = state.Active.Platform;
        state.Active = null;
    }

    private PlaytimeState View(Stored state, DateTimeOffset instant, bool running)
    {
        string today = TimeZoneInfo.ConvertTime(instant, localTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new()
        {
            TotalSeconds = state.TotalTicks / TimeSpan.TicksPerSecond,
            TodaySeconds = state.Days.GetValueOrDefault(today) / TimeSpan.TicksPerSecond,
            CurrentSessionSeconds = running && state.Active != null ? state.Active.CountedTicks / TimeSpan.TicksPerSecond : 0,
            LastSessionSeconds = state.LastSessionTicks / TimeSpan.TicksPerSecond,
            IsRunning = running && state.Active != null,
            Platform = state.Active?.Platform ?? state.LastPlatform,
            Days = state.Days.OrderBy(pair => pair.Key, StringComparer.Ordinal).TakeLast(365)
                .Select(pair => new PlaytimeDay { Date = pair.Key, Seconds = pair.Value / TimeSpan.TicksPerSecond }).ToList()
        };
    }

    private Stored Load()
    {
        NoLinks(statePath);
        if (!File.Exists(statePath)) return new();
        try
        {
            using var file = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxFileBytes) return new();
            using var reader = new StreamReader(file, Encoding.UTF8);
            var state = JsonConvert.DeserializeObject<Stored>(reader.ReadToEnd());
            if (state == null || state.TotalTicks is < 0 or > MaxTotalTicks || state.LastSessionTicks is < 0 or > MaxTotalTicks || state.LastSessionTicks > state.TotalTicks
                || state.Days == null || state.Days.Count > 36600) return new();
            long sum = 0;
            foreach (var day in state.Days)
            {
                if (!DateOnly.TryParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || day.Value is < 0 or > 2 * TimeSpan.TicksPerDay) return new();
                sum = checked(sum + day.Value);
            }
            if (sum != state.TotalTicks) return new();
            state.LastPlatform ??= "";
            state.LastPlatform ??= "";
            if (state.LastPlatform.Length > 0) state.LastPlatform = NormalizePlatform(state.LastPlatform);
            if (state.Active != null)
            {
                try
                {
                    var active = state.Active;
                    if (active.Pid <= 0 || active.ProcessStartUtc == DateTimeOffset.MinValue || active.LastCheckpointUtc == DateTimeOffset.MinValue
                        || active.CountedTicks < 0 || active.CountedTicks > state.TotalTicks) throw new ArgumentException("会话信息无效。");
                    active.Platform = NormalizePlatform(active.Platform);
                    active.GameDirectory = NormalizeGameDirectory(active.GameDirectory);
                    active.ProcessStartUtc = active.ProcessStartUtc.ToUniversalTime();
                    active.LastCheckpointUtc = active.LastCheckpointUtc.ToUniversalTime();
                }
                catch (ArgumentException) { state.Active = null; }
            }
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException)
        { return new(); }
    }

    private void Save(Stored state)
    {
        NoLinks(statePath);
        string temporary = Path.Combine(Path.GetDirectoryName(statePath)!, ".playtime-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state));
            if (bytes.Length > MaxFileBytes) throw new IOException("游戏时长记录超过保存上限。");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            NoLinks(statePath); File.Move(temporary, statePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Stored Copy(Stored state) => new()
    {
        TotalTicks = state.TotalTicks, LastSessionTicks = state.LastSessionTicks, LastPlatform = state.LastPlatform,
        Days = new(state.Days, StringComparer.Ordinal),
        Active = state.Active == null ? null : new()
        {
            Pid = state.Active.Pid, ProcessStartUtc = state.Active.ProcessStartUtc, GameDirectory = state.Active.GameDirectory,
            Platform = state.Active.Platform, LastCheckpointUtc = state.Active.LastCheckpointUtc, CountedTicks = state.Active.CountedTicks
        }
    };
    private static string NormalizePlatform(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        if (value is not ("steam" or "xbox" or "offline")) throw new ArgumentException("游戏平台需要选择 Steam、Xbox 或独立离线版。");
        return value;
    }
    private static string NormalizeGameDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("游戏目录无效。");
        return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("游戏时长记录不能使用符号链接或目录联接。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private sealed class Stored
    {
        public long TotalTicks { get; set; }
        public long LastSessionTicks { get; set; }
        public string LastPlatform { get; set; } = "";
        public Dictionary<string, long> Days { get; set; } = new(StringComparer.Ordinal);
        public StoredActive? Active { get; set; }
    }
    private sealed class StoredActive
    {
        public int Pid { get; set; }
        public DateTimeOffset ProcessStartUtc { get; set; }
        public string GameDirectory { get; set; } = "";
        public string Platform { get; set; } = "";
        public DateTimeOffset LastCheckpointUtc { get; set; }
        public long CountedTicks { get; set; }
    }
}
