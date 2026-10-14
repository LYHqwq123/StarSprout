namespace StarSprout.Services;

/// <summary>Root host must verify both PID and UTC process start time before counting a restored session.</summary>
public sealed class PlaytimeActiveSession
{
    public int Pid { get; set; }
    public DateTimeOffset ProcessStartUtc { get; set; }
    public string GameDirectory { get; set; } = "";
    public string Platform { get; set; } = "";
}

public sealed class PlaytimeDay
{
    public string Date { get; set; } = "";
    public long Seconds { get; set; }
}

public sealed class PlaytimeState
{
    public long TotalSeconds { get; set; }
    public long TodaySeconds { get; set; }
    public long CurrentSessionSeconds { get; set; }
    public long LastSessionSeconds { get; set; }
    public bool IsRunning { get; set; }
    public string Platform { get; set; } = "";
    public List<PlaytimeDay> Days { get; set; } = new();
}
