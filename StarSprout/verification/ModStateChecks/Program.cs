using System.Security.Cryptography;
using Newtonsoft.Json;
using StarSprout.Services;

var allowedRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
var root = Path.Combine(allowedRoot, "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; }
bool Reject(Action action)
{
    try { action(); return false; }
    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { return true; }
}
(StardewService Game, ModTroubleshootingService Trouble, string Directory, string Settings) Fixture(string name)
{
    var directory = Path.Combine(root, name, "game");
    var settings = Path.Combine(root, name, "settings");
    Directory.CreateDirectory(Path.Combine(directory, "Mods"));
    // An empty fixture executable proves folder recognition without starting any process.
    File.WriteAllBytes(Path.Combine(directory, "Stardew Valley.exe"), []);
    var game = new StardewService(settings, Path.Combine(root, name, "saves"));
    game.SetGameDirectory(directory);
    return (game, new ModTroubleshootingService(game, settings), directory, settings);
}
string Add(StardewService game, string relative, string id)
{
    var directory = Path.Combine(game.Settings.GameDirectory, "Mods", relative);
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonConvert.SerializeObject(new
    {
        Name = id, Author = "Fixture", Version = "1.0.0", Description = "Isolated conflict test fixture",
        UniqueID = id, ContentPackFor = new { UniqueID = "Pathoschild.ContentPatcher" }
    }));
    File.WriteAllText(Path.Combine(directory, "content.json"), "{\"fixture\":true}");
    return directory;
}
StardewMod Mod(StardewService game, string id) => game.GetMods().Single(m => m.UniqueId == id);
Dictionary<string, bool> States(StardewService game) => game.GetMods().ToDictionary(m => m.UniqueId, m => m.Enabled);
bool Same(Dictionary<string, bool> left, Dictionary<string, bool> right) => left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var value) && value == p.Value);
Dictionary<string, string> ManifestHashes(StardewService game) => game.GetMods().ToDictionary(m => m.UniqueId, m => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(m.DirectoryPath, "manifest.json")))));
try
{
    var main = Fixture("mixed");
    Add(main.Game, "Alpha", "Fixture.Alpha"); Add(main.Game, "Beta", "Fixture.Beta");
    Add(main.Game, Path.Combine("Group", "Gamma"), "Fixture.Gamma");
    Add(main.Game, ".InitiallyOff", "Fixture.InitiallyOff");
    Add(main.Game, Path.Combine(".Cold", "Delta"), "Fixture.Delta");
    var initial = States(main.Game);
    var initialManifests = ManifestHashes(main.Game);
    Check(initial.Count == 5 && initial.Count(p => p.Value) == 3, "mixed direct/nested initial enabled states");
    main.Trouble.SetEnabled([Mod(main.Game, "Fixture.Alpha").DirectoryPath], false);
    Check(!Mod(main.Game, "Fixture.Alpha").Enabled && Mod(main.Game, "Fixture.Beta").Enabled && main.Trouble.GetState().CanRestore, "single disable captures combination");
    main.Trouble.SetEnabled([Mod(main.Game, "Fixture.Beta").DirectoryPath, Mod(main.Game, "Fixture.Gamma").DirectoryPath], false);
    Check(main.Game.GetMods().All(m => !m.Enabled), "bulk disable works inside an active nested group");
    main.Trouble.SetEnabled([Mod(main.Game, "Fixture.Beta").DirectoryPath], true);
    main.Trouble.DisableAll();
    Check(main.Game.GetMods().All(m => !m.Enabled) && main.Trouble.GetState().DisabledCount == 5, "disable all retains initially disabled mods");
    main.Trouble.SetEnabled([Mod(main.Game, "Fixture.Beta").DirectoryPath], true);
    main.Trouble.Restore();
    Check(Same(initial, States(main.Game)) && !main.Trouble.GetState().CanRestore, "partial manual reenable restores original mixed combination and clears record");
    Check(initialManifests.All(p => ManifestHashes(main.Game)[p.Key] == p.Value), "disable/restore never modifies manifests");
    Check(main.Game.GetMods().Count == 5 && Directory.GetFiles(Path.Combine(main.Directory, "Mods"), "content.json", SearchOption.AllDirectories).Length == 5, "disable/restore never removes mods or assets");

    main.Trouble.DisableAll();
    var restartedGame = new StardewService(main.Settings, Path.Combine(root, "mixed", "saves"));
    var restarted = new ModTroubleshootingService(restartedGame, main.Settings);
    Check(restarted.GetState().CanRestore && restarted.GetState().DisabledCount == 5, "restart loads pending initial combination");
    restarted.Restore();
    Check(Same(initial, States(restartedGame)), "restart restores original combination");

    main.Trouble.DisableAll();
    var other = Fixture("other"); Add(other.Game, "Other", "Fixture.Other");
    main.Game.SetGameDirectory(other.Directory);
    Check(!main.Trouble.GetState().CanRestore && Reject(main.Trouble.Restore) && Mod(main.Game, "Fixture.Other").Enabled, "switch directory never restores the wrong target");
    main.Game.SetGameDirectory(main.Directory);
    main.Trouble.Restore();
    Check(Same(initial, States(main.Game)), "returning to original directory preserves its restore record");

    main.Trouble.DisableAll();
    main.Game.SetGameDirectory(other.Directory);
    main.Trouble.DisableAll();
    main.Game.SetGameDirectory(main.Directory);
    main.Trouble.Restore();
    Check(Same(initial, States(main.Game)), "changes in another game directory cannot overwrite original restore combination");

    var collision = Fixture("collision");
    var alpha = Add(collision.Game, "Alpha", "Collision.Alpha");
    var beta = Add(collision.Game, "Beta", "Collision.Beta");
    var blocked = Path.Combine(collision.Directory, "Mods", ".Beta"); Directory.CreateDirectory(blocked);
    File.WriteAllText(Path.Combine(blocked, "keep.txt"), "untouched");
    Check(Reject(() => collision.Game.SetModStates(new Dictionary<string, bool> { [alpha] = false, [beta] = false })) && Mod(collision.Game, "Collision.Alpha").Enabled && Mod(collision.Game, "Collision.Beta").Enabled && File.ReadAllText(Path.Combine(blocked, "keep.txt")) == "untouched", "destination collision prevalidation prevents partial mutation");
    var outside = Add(other.Game, "Outside", "Fixture.Outside");
    Check(Reject(() => collision.Game.SetModStates(new Dictionary<string, bool> { [alpha] = false, [outside] = false })) && Mod(collision.Game, "Collision.Alpha").Enabled && Mod(other.Game, "Fixture.Outside").Enabled, "external mod path rejected before any valid selection is changed");
    Check(Reject(() => collision.Game.SetModStates(new Dictionary<string, bool> { [alpha] = false, [Path.Combine(collision.Directory, "Mods", "Stale")] = false })) && Mod(collision.Game, "Collision.Alpha").Enabled, "stale selection rejected before any valid selection is changed");
    Check(Reject(() => main.Game.SetModStates(new Dictionary<string, bool> { [Mod(main.Game, "Fixture.Alpha").DirectoryPath] = false, [Mod(main.Game, "Fixture.Delta").DirectoryPath] = true })) && Mod(main.Game, "Fixture.Alpha").Enabled && !Mod(main.Game, "Fixture.Delta").Enabled, "disabled ancestor group cannot be enabled by partial bulk rename");
    collision.Game.SetModStates(new Dictionary<string, bool> { [alpha] = true });
    Check(Directory.Exists(alpha) && ManifestHashes(collision.Game).Count == 2, "idempotent enabled request preserves folder and manifest");

    var changed = Fixture("changed-manifest");
    Add(changed.Game, "First", "Changed.First"); Add(changed.Game, "Second", "Changed.Second");
    changed.Trouble.DisableAll();
    var manifest = Path.Combine(Mod(changed.Game, "Changed.Second").DirectoryPath, "manifest.json");
    var originalManifest = File.ReadAllText(manifest);
    File.WriteAllText(manifest, originalManifest.Replace("Changed.Second", "Changed.Replaced"));
    Check(Reject(changed.Trouble.Restore) && changed.Game.GetMods().All(m => !m.Enabled) && changed.Trouble.GetState().CanRestore, "changed manifest identity rejects entire restore without partial enable");
    File.WriteAllText(manifest, originalManifest);
    changed.Trouble.Restore();
    Check(changed.Game.GetMods().All(m => m.Enabled) && !changed.Trouble.GetState().CanRestore, "repairing changed manifest permits original restoration");
    var removal = Fixture("removal");
    var removeEnabled = Add(removal.Game, "Enabled", "Removal.Enabled");
    var removeDisabled = Add(removal.Game, ".Disabled", "Removal.Disabled");
    File.WriteAllText(Path.Combine(removeEnabled, "config.json"), "{\"keep\":true}");
    var removed = removal.Game.RemoveMod(removeEnabled);
    Check(!Directory.Exists(removeEnabled) && removal.Game.GetMods().Count == 1, "delete removes selected mod from installed scan");
    Check(File.ReadAllText(Path.Combine(removed.BackupDirectory, "config.json")) == "{\"keep\":true}" && File.ReadAllText(Path.Combine(removed.BackupDirectory, "content.json")) == "{\"fixture\":true}", "deleted mod backup retains complete config and data");
    Check(File.Exists(Path.Combine(Path.GetDirectoryName(removed.BackupDirectory)!, "restore-info.json")), "deletion retains recoverable original path and identity receipt");
    Check(Reject(() => removal.Game.RemoveMod(removeEnabled)), "stale deletion cannot affect another mod");
    Check(Reject(() => removal.Game.RemoveMod(Path.Combine(removal.Directory, "Mods"))), "cannot delete Mods root");
    Check(Reject(() => removal.Game.RemoveMod(outside)) && Directory.Exists(outside), "cannot delete mod from another game");
    var groupMod = Add(removal.Game, Path.Combine("Group", "Nested"), "Removal.Nested");
    Check(Reject(() => removal.Game.RemoveMod(Path.GetDirectoryName(groupMod)!)) && Directory.Exists(groupMod), "cannot delete group parent not identified as a mod");
    var removedDisabled = removal.Game.RemoveMod(removeDisabled);
    Check(!Directory.Exists(removeDisabled) && Path.GetFileName(removedDisabled.BackupDirectory) == ".Disabled", "disabled mod can be removed with original disabled name retained");
    var invalid = Add(removal.Game, "Invalid", "Removal.Invalid");
    File.WriteAllText(Path.Combine(invalid, "manifest.json"), "broken manifest");
    Check(removal.Game.RemoveMod(invalid).Name == "Invalid" && !Directory.Exists(invalid), "invalid manifest mod can be deleted safely for conflict cleanup");
    var lockFolder = Add(removal.Game, "Locked", "Removal.Locked");
    using (var held = new FileStream(Path.Combine(lockFolder, "content.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try
        {
            var lockedRemoval = removal.Game.RemoveMod(lockFolder);
            Check(!Directory.Exists(lockFolder) && File.Exists(Path.Combine(lockedRemoval.BackupDirectory, "content.json")), "locked cleanup still retains verified backup outside Mods");
        }
        catch (IOException) { Check(Directory.Exists(lockFolder) && File.Exists(Path.Combine(lockFolder, "manifest.json")), "failed locked removal preserves original directory"); }
    }
    Console.WriteLine($"PASS {passed} mod-state checks; isolated folders only, no game process started.");
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path");
    Directory.Delete(resolved, true);
}
