using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StarSprout.Services;

string root = Path.Combine(AppContext.BaseDirectory, "xbox-launch-check-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
string game = Path.Combine(root, "Xbox game");
string other = Path.Combine(root, "Other installation");
Directory.CreateDirectory(game);
Directory.CreateDirectory(other);
File.WriteAllBytes(Path.Combine(game, "Stardew Valley.exe"), Array.Empty<byte>());
File.WriteAllBytes(Path.Combine(game, "StardewModdingAPI.exe"), Array.Empty<byte>());
File.WriteAllBytes(Path.Combine(other, "Stardew Valley.exe"), Array.Empty<byte>());
JObject Record() => new()
{
    ["Name"] = "ConcernedApe.StardewValleyPC",
    ["PackageFamilyName"] = "ConcernedApe.StardewValleyPC_0c8vynj4cqe4e",
    ["ApplicationId"] = "App",
    ["InstallLocation"] = game,
    ["Executable"] = "Stardew Valley.exe"
};
XboxGameLaunchService Mock(string json) => new(_ => Task.FromResult(json));
Task<ProcessStartInfo?> Plan(JObject record) => Mock(new JArray(record).ToString(Formatting.None)).GetGameStartInfoAsync(game);
try
{
    var plan = await Plan(Record());
    Check(plan != null, "matching registered package resolves launch plan");
    Check(plan!.FileName == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "fixed Windows Explorer executable only");
    Check(!plan.UseShellExecute && plan.CreateNoWindow && plan.WindowStyle == ProcessWindowStyle.Hidden, "hidden no-shell activation helper plan");
    Check(plan.ArgumentList.Count == 1 && plan.ArgumentList[0] == "shell:AppsFolder\\ConcernedApe.StardewValleyPC_0c8vynj4cqe4e!App", "one validated package application argument");
    Check(plan.Arguments.Length == 0, "no interpolated shell argument string");

    var smapi = Record();
    smapi["Executable"] = "StardewModdingAPI.exe";
    Check(await Plan(smapi) != null, "registered SMAPI entry may activate official package");
    var sub = Path.Combine(game, "Content");
    Directory.CreateDirectory(sub);
    File.WriteAllBytes(Path.Combine(sub, "Stardew Valley.exe"), Array.Empty<byte>());
    var nested = Record();
    nested["Executable"] = "Content\\Stardew Valley.exe";
    Check(await Mock(new JArray(nested).ToString()).GetGameStartInfoAsync(sub) != null, "registered Content subdirectory matches selected executable directory");

    foreach (var (field, value) in new[]
    {
        ("Name", "Different.Game"), ("PackageFamilyName", "OtherGame_0c8vynj4cqe4e"),
        ("PackageFamilyName", "ConcernedApe.StardewValleyPC_0c8vynj4cqe4e;calc.exe"),
        ("ApplicationId", "App&calc.exe"), ("ApplicationId", "../App"), ("ApplicationId", "App\" --bad"),
        ("InstallLocation", other), ("InstallLocation", "\\\\server\\share"),
        ("Executable", "cmd.exe"), ("Executable", Path.Combine(other, "Stardew Valley.exe")),
        ("Executable", "Stardew Valley.exe:stream"), ("Executable", "Stardew Valley.exe\n")
    })
    {
        var bad = Record();
        bad[field] = value;
        Check(await Plan(bad) == null, "unsafe or unrelated registration rejected: " + field + "=" + value);
    }
    var escaping = Record();
    escaping["InstallLocation"] = other;
    escaping["Executable"] = "..\\Xbox game\\Stardew Valley.exe";
    Check(await Plan(escaping) == null, "manifest cannot escape registered package location to another installation");
    Check(await Mock("[]").GetGameStartInfoAsync(game) == null, "unregistered Xbox game falls back to official client");
    Check(await Mock("{invalid").GetGameStartInfoAsync(game) == null, "malformed discovery falls back");
    Check(await Mock(Record().ToString()).GetGameStartInfoAsync(game) == null, "unexpected discovery shape refused");
    Check(await Mock(new string('x', 1024 * 1024 + 1)).GetGameStartInfoAsync(game) == null, "discovery output bounded");
    Check(await Mock(new JArray(Enumerable.Range(0, 101).Select(_ => Record())).ToString()).GetGameStartInfoAsync(game) == null, "registration count bounded");
    var inaccessible = new XboxGameLaunchService(_ => throw new UnauthorizedAccessException());
    Check(await inaccessible.GetGameStartInfoAsync(game) == null, "inaccessible discovery falls back without claiming activation");

    int queries = 0;
    var neverQuery = new XboxGameLaunchService(_ => { queries++; return Task.FromResult("[]"); });
    foreach (var path in new[] { "", "relative-folder", "\\\\server\\share", Path.Combine(root, "missing") })
        Check(await neverQuery.GetGameStartInfoAsync(path) == null, "invalid selected installation refused before discovery");
    Check(queries == 0, "invalid folders never query packages");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { await neverQuery.GetGameStartInfoAsync(game, cancelled.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { passed++; }
    }
    Check(queries == 0, "cancelled request never queries packages");
    using (var cancelDuring = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
    {
        var waiting = new XboxGameLaunchService(async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "[]"; });
        try { await waiting.GetGameStartInfoAsync(game, cancelDuring.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { passed++; }
    }

    if (args.Contains("--live"))
    {
        // Read-only system query: fake selected folder cannot match a real registered package.
        Check(await new XboxGameLaunchService().GetGameStartInfoAsync(game) == null, "live registered package query rejects test-owned unrelated game directory");
    }
    Console.WriteLine($"XboxLaunchChecks: {passed} checks passed; no Xbox clients or games were activated.");
}
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(root, true);
}
