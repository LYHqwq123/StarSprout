using StarSprout.Services;

string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
DateTimeOffset instant = DateTimeOffset.Parse("2026-10-14T12:00:00+08:00");
string game = Path.Combine(root, "Game");
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; }
void Reject(Action action, string message)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { checks++; return; }
    throw new Exception("FAIL: expected rejection: " + message);
}
PlaytimeService Create(string name, TimeZoneInfo? zone = null) => new(Path.Combine(root, name), () => instant, zone);

var clock = Create("basic");
Check(clock.GetState().TotalSeconds == 0 && clock.GetState().Days.Count == 0 && clock.ActiveSession == null, "new playtime never infers historical calendar time");
Reject(() => clock.BeginSession(game, "other", 7, instant), "unsupported platform");
Reject(() => clock.BeginSession(game, "steam", 0, instant), "invalid PID");
Reject(() => clock.BeginSession(game, "steam", 7, DateTimeOffset.MinValue), "missing process start identity");
Reject(() => clock.BeginSession("bad\npath", "steam", 7, instant), "invalid game path");
var processStart = instant.AddHours(-2);
clock.BeginSession(game, "steam", 7, processStart);
Check(clock.GetState().IsRunning && clock.GetState().TotalSeconds == 0, "observation starts now rather than claiming earlier process lifetime");
Check(clock.ActiveSession!.Pid == 7 && clock.ActiveSession.ProcessStartUtc == processStart && clock.ActiveSession.Platform == "steam", "PID and UTC process start are exposed for host verification");
var metadata = clock.ActiveSession;
metadata!.Pid = 999;
Check(clock.ActiveSession.Pid == 7, "caller cannot mutate persisted active metadata");
instant = instant.AddMilliseconds(500);
Check(clock.GetState().TotalSeconds == 0, "partial seconds do not round up");
clock.Checkpoint(true);
instant = instant.AddMilliseconds(600);
Check(clock.Checkpoint(true).TotalSeconds == 1, "subsecond checkpoint fractions accumulate correctly");
instant = instant.AddSeconds(4);
Check(clock.GetState().CurrentSessionSeconds == 5 && clock.GetState().TodaySeconds == 5, "verified active session interpolates for homepage display");
Check(Create("basic").GetState().TotalSeconds == 1, "display interpolation does not persist unobserved time");
clock.Checkpoint(true);
Check(clock.GetState().Days.Single().Date == "2026-10-14", "daily totals use local date");
clock.BeginSession(game, "steam", 7, processStart);
Check(clock.GetState().TotalSeconds == 5, "same verified session cannot double-count");
Reject(() => clock.BeginSession(game, "xbox", 8, instant), "replacing a still-active session requires explicit reconciliation");
Check(clock.EndSession().LastSessionSeconds == 5 && !clock.GetState().IsRunning && clock.ActiveSession == null, "unknown session end keeps last confirmed checkpoint");
Check(clock.EndSession().LastSessionSeconds == 5, "ending an absent session is idempotent");
clock.BeginSession(game, "xbox", 8, instant);
instant = instant.AddSeconds(3);
clock.Checkpoint(true);
clock.EndSession();
Check(clock.GetState().TotalSeconds == 8 && clock.GetState().LastSessionSeconds == 3 && clock.GetState().Platform == "xbox", "multiple platforms accumulate one lifetime total and preserve last session");

instant = DateTimeOffset.Parse("2026-10-14T23:59:50+08:00");
var midnight = Create("midnight");
midnight.BeginSession(game, "offline", 10, instant);
instant = instant.AddSeconds(20);
var crossed = midnight.Checkpoint(true);
Check(crossed.TotalSeconds == 20 && crossed.TodaySeconds == 10, "midnight splits elapsed time into correct local days");
Check(crossed.Days.Count == 2 && crossed.Days[0].Seconds == 10 && crossed.Days[1].Seconds == 10, "both sides of midnight retain exact seconds");
Check(crossed.Platform == "offline", "independent offline game sessions are supported");

instant = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
var backwards = Create("backwards");
backwards.BeginSession(game, "steam", 20, instant);
instant = instant.AddSeconds(15);
backwards.Checkpoint(true);
instant = instant.AddSeconds(-10);
Check(backwards.Checkpoint(true).TotalSeconds == 15, "backwards wall clock never subtracts or adds time");
instant = instant.AddSeconds(15);
Check(backwards.Checkpoint(true).TotalSeconds == 20, "clock recovery resumes from high-water mark without double count");

instant = DateTimeOffset.Parse("2026-10-14T14:00:00Z");
var original = Create("restart");
var identity = instant;
original.BeginSession(game, "steam", 30, identity);
instant = instant.AddSeconds(10);
original.Checkpoint(true);
instant = instant.AddSeconds(50);
var restored = Create("restart");
Check(!restored.GetState().IsRunning && restored.GetState().TotalSeconds == 10 && restored.ActiveSession!.Pid == 30, "restart exposes pending identity but adds no unverified downtime");
Check(restored.Checkpoint(true).TotalSeconds == 60 && restored.GetState().CurrentSessionSeconds == 60, "verified surviving process recovers launcher downtime once");
Check(restored.Checkpoint(true).TotalSeconds == 60, "repeated verification at same instant does not duplicate gap");
instant = instant.AddSeconds(100);
var lost = Create("restart");
Check(lost.Checkpoint(false).TotalSeconds == 60 && lost.GetState().LastSessionSeconds == 60, "dead or unknown process keeps only last confirmed time");
Check(lost.ActiveSession == null, "stale session metadata clears after negative reconciliation");
lost.BeginSession(game, "steam", 30, instant); // Simulate reused PID with a different start time.
instant = instant.AddSeconds(5);
lost.Checkpoint(true);
instant = instant.AddHours(1);
var reused = Create("restart");
bool identityMatches = reused.ActiveSession!.Pid == 30 && reused.ActiveSession.ProcessStartUtc == instant;
Check(!identityMatches, "PID alone is insufficient for restored identity");
Check(reused.Checkpoint(identityMatches).TotalSeconds == 65, "a reused PID cannot fabricate one hour of downtime");

instant = DateTimeOffset.Parse("2026-10-14T17:00:00Z");
var exactExit = Create("exact-exit");
var exitStart = instant;
exactExit.BeginSession(game, "xbox", 40, instant);
instant = instant.AddSeconds(10);
exactExit.Checkpoint(true);
instant = instant.AddSeconds(20);
var ended = exactExit.FinishSession(exitStart.AddSeconds(12.5));
Check(ended.TotalSeconds == 12 && ended.LastSessionSeconds == 12 && !ended.IsRunning, "verified actual exit time counts exact final interval instead of polling delay");
Check(Create("exact-exit").GetState().TotalSeconds == 12, "exact final interval persists after restart");
var futureExit = Create("future-exit");
futureExit.BeginSession(game, "steam", 41, instant);
instant = instant.AddSeconds(3);
Check(futureExit.FinishSession(instant.AddHours(1)).TotalSeconds == 3, "future exit timestamp is clamped to current time");
var oldExit = Create("old-exit");
oldExit.BeginSession(game, "steam", 42, instant);
instant = instant.AddSeconds(5);
oldExit.Checkpoint(true);
Check(oldExit.FinishSession(instant.AddHours(-1)).TotalSeconds == 5, "older exit timestamp cannot erase or duplicate confirmed time");

if (TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var eastern))
{
    instant = DateTimeOffset.Parse("2026-03-08T00:00:00-05:00");
    var spring = Create("spring-dst", eastern);
    spring.BeginSession(game, "steam", 50, instant);
    instant = DateTimeOffset.Parse("2026-03-09T00:00:00-04:00");
    var state = spring.Checkpoint(true);
    Check(state.TotalSeconds == 23 * 3600 && state.Days.Single().Date == "2026-03-08", "spring DST day uses 23 actual hours rather than nominal 24");
    instant = DateTimeOffset.Parse("2026-11-01T00:00:00-04:00");
    var fall = Create("fall-dst", eastern);
    fall.BeginSession(game, "steam", 51, instant);
    instant = DateTimeOffset.Parse("2026-11-02T00:00:00-05:00");
    Check(fall.Checkpoint(true).TotalSeconds == 25 * 3600, "fall DST day counts repeated clock hour exactly once in UTC");
}

string corruptPath = Path.Combine(root, "corrupt");
Directory.CreateDirectory(corruptPath);
File.WriteAllText(Path.Combine(corruptPath, "playtime.json"), "{broken");
Check(new PlaytimeService(corruptPath, () => instant).GetState().TotalSeconds == 0, "malformed local history cannot create played time");
File.WriteAllText(Path.Combine(corruptPath, "playtime.json"), "{\"TotalTicks\":999999,\"Days\":{}}");
Check(new PlaytimeService(corruptPath, () => instant).GetState().TotalSeconds == 0, "inconsistent aggregate is rejected");
Check(Directory.EnumerateFiles(root, ".playtime-*.tmp", SearchOption.AllDirectories).Count() == 0, "atomic writes leave no transient state files");
Console.WriteLine($"PASS: {checks} playtime checks. Fixtures: {root}");
