using System.IO.Compression;
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
(StardewService Game, string Mods, string Settings) Fixture(string name)
{
    var directory = Path.Combine(root, name, "game");
    var settings = Path.Combine(root, name, "settings");
    var mods = Path.Combine(directory, "Mods");
    Directory.CreateDirectory(mods);
    // Recognition fixture only: no executable or DLL is ever run.
    File.WriteAllBytes(Path.Combine(directory, "Stardew Valley.exe"), []);
    var game = new StardewService(settings, Path.Combine(root, name, "saves"));
    game.SetGameDirectory(directory);
    return (game, mods, settings);
}
string Manifest(string id, string version) => JsonConvert.SerializeObject(new
{
    Name = id, Author = "Fixture", Version = version, Description = "Isolated installation fixture",
    UniqueID = id, EntryDll = "fixture.dll"
});
Dictionary<string, string> Package(string folder, string id, string version, string payload = "fixture-not-executable")
{
    var prefix = folder.Length == 0 ? "" : folder + "/";
    return new Dictionary<string, string> { [prefix + "manifest.json"] = Manifest(id, version), [prefix + "fixture.dll"] = payload };
}
string Zip(string label, Dictionary<string, string> files, Action<ZipArchive>? extra = null)
{
    var path = Path.Combine(root, label + ".zip");
    using var stream = File.Create(path);
    using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
    foreach (var (name, text) in files)
    {
        var entry = zip.CreateEntry(name);
        using var output = new StreamWriter(entry.Open());
        output.Write(text);
    }
    extra?.Invoke(zip);
    return path;
}
string Add(StardewService game, string folder, string id, string version = "1.0.0")
{
    var directory = Path.Combine(game.Settings.GameDirectory, "Mods", folder);
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "manifest.json"), Manifest(id, version));
    File.WriteAllText(Path.Combine(directory, "fixture.dll"), "old-fixture-not-executable");
    return directory;
}
Dictionary<string, string> Tree(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
    .ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);
bool Same(Dictionary<string, string> left, Dictionary<string, string> right) => left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var value) && value == p.Value);
StardewMod Mod(StardewService game, string id) => game.GetMods().Single(m => m.UniqueId == id);
try
{
    var basic = Fixture("basic");
    var initialZip = Zip("new", Package("Alpha", "Fixture.Alpha", "1.0.0"));
    var first = basic.Game.InstallModZipAutomatically(initialZip);
    Check(first.InstalledCount == 1 && first.UpdatedCount == 0 && first.SkippedCount == 0 && first.Mods.Count == 1, "new mod installs without a second operation");
    Check(File.ReadAllText(Path.Combine(Mod(basic.Game, "Fixture.Alpha").DirectoryPath, "fixture.dll")) == "fixture-not-executable", "downloaded DLL bytes are copied without execution");
    var initial = Tree(basic.Mods);
    var repeated = basic.Game.InstallModZipAutomatically(initialZip);
    Check(repeated.SkippedCount == 1 && repeated.InstalledCount == 0 && repeated.BackupDirectory == "" && Same(initial, Tree(basic.Mods)), "same package is an idempotent successful result");
    var changedSame = basic.Game.InstallModZipAutomatically(Zip("same-version-different-payload", Package("OtherName", "Fixture.Alpha", "1.0.0", "changed-but-same-version")));
    Check(changedSame.SkippedCount == 1 && changedSame.Warnings.Any(w => w.Contains("相同版本")) && Same(initial, Tree(basic.Mods)), "same version with different archive leaves old bytes intact and explains the skip");
    Check(Reject(() => basic.Game.InstallModZip(initialZip)) && Same(initial, Tree(basic.Mods)), "manual import retains its existing refuse-overwrite behavior");

    var update = Fixture("update");
    var old = Add(update.Game, Path.Combine(".Group", ".OldAlpha"), "Update.Alpha");
    File.WriteAllText(Path.Combine(old, "config.json"), "{\"userSetting\":42}");
    Directory.CreateDirectory(Path.Combine(old, "nested"));
    File.WriteAllText(Path.Combine(old, "nested", "config.json"), "{\"nestedUserSetting\":true}");
    File.WriteAllText(Path.Combine(old, "obsolete.dll"), "old-obsolete");
    Directory.CreateDirectory(Path.Combine(old, "empty-saved-folder"));
    var oldTree = Tree(old);
    var upgrade = Package("RenamedByAuthor", "Update.Alpha", "2.0.0", "new-fixture-not-executable");
    upgrade["RenamedByAuthor/config.json"] = "{\"defaultSetting\":0}";
    var upgraded = update.Game.InstallModZipAutomatically(Zip("upgrade", upgrade));
    Check(upgraded.UpdatedCount == 1 && upgraded.InstalledCount == 0 && Directory.Exists(upgraded.BackupDirectory), "older identity is updated after making a persistent backup");
    Check(Mod(update.Game, "Update.Alpha").DirectoryPath == old && !Mod(update.Game, "Update.Alpha").Enabled, "update preserves original directory name and disabled parent/child state");
    Check(File.ReadAllText(Path.Combine(old, "config.json")) == "{\"userSetting\":42}" && File.ReadAllText(Path.Combine(old, "nested", "config.json")) == "{\"nestedUserSetting\":true}", "root and nested user config.json override new defaults");
    Check(!File.Exists(Path.Combine(old, "obsolete.dll")) && File.ReadAllText(Path.Combine(old, "fixture.dll")) == "new-fixture-not-executable", "obsolete DLL is removed from the active tree rather than carried into the update");
    var backup = Directory.GetDirectories(upgraded.BackupDirectory).Single();
    Check(Same(oldTree, Tree(backup)) && Directory.Exists(Path.Combine(backup, "empty-saved-folder")) && File.ReadAllText(Path.Combine(upgraded.BackupDirectory, "restore-info.json")).Contains(old.Replace("\\", "\\\\")), "verified backup contains every old file, empty directory and original restoration path");
    var updatedTree = Tree(update.Mods);
    var downgrade = update.Game.InstallModZipAutomatically(Zip("downgrade", Package("Alpha", "Update.Alpha", "1.5.0")));
    Check(downgrade.SkippedCount == 1 && downgrade.Warnings.Any(w => w.Contains("未降级")) && Same(updatedTree, Tree(update.Mods)), "older downloads cannot silently downgrade an installed mod");

    var versions = Fixture("versions");
    Add(versions.Game, "Preview", "Version.Preview", "1.2.0-beta.9");
    Check(versions.Game.InstallModZipAutomatically(Zip("stable", Package("Preview", "Version.Preview", "1.2.0"))).UpdatedCount == 1, "stable release correctly supersedes its prerelease");
    Add(versions.Game, "Huge", "Version.Huge", "999999999999999999999999999999.0.0");
    Check(versions.Game.InstallModZipAutomatically(Zip("huge-downgrade", Package("Huge", "Version.Huge", "2.0.0"))).SkippedCount == 1 && Mod(versions.Game, "Version.Huge").Version.StartsWith("999"), "large valid version components do not overflow downgrade protection");
    var rootPack = Fixture("root-manifest");
    var rootInstalled = rootPack.Game.InstallModZipAutomatically(Zip("RootArchive", Package("", "Root.Mod", "1.0.0")));
    Check(rootInstalled.InstalledCount == 1 && Path.GetFileName(Mod(rootPack.Game, "Root.Mod").DirectoryPath) == "RootArchive", "archive-root manifest uses a safe archive-derived folder");

    var safe = Fixture("unsafe-packages");
    Add(safe.Game, "Untouched", "Safety.Untouched");
    var safeBefore = Tree(safe.Mods);
    var traversal = Package("Alpha", "Safety.Alpha", "1.0.0"); traversal["../escaped.txt"] = "bad";
    Check(Reject(() => safe.Game.InstallModZipAutomatically(Zip("traversal", traversal))) && Same(safeBefore, Tree(safe.Mods)) && !File.Exists(Path.Combine(safe.Mods, "escaped.txt")), "ZIP traversal is rejected without touching installed mods");
    Check(Reject(() => safe.Game.InstallModZipAutomatically(Zip("duplicate-path", Package("Alpha", "Safety.Alpha", "1.0.0"), z => z.CreateEntry("Alpha/FIXTURE.DLL")))) && Same(safeBefore, Tree(safe.Mods)), "case-conflicting ZIP entries are rejected before any installation");
    Check(Reject(() => safe.Game.InstallModZipAutomatically(Zip("zip-link", Package("Alpha", "Safety.Alpha", "1.0.0"), z => { var link = z.CreateEntry("link"); link.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); }))) && Same(safeBefore, Tree(safe.Mods)), "ZIP symlink entries are rejected before any installation");
    var invalid = Package("Alpha", "Safety.Alpha", "1.0.0"); invalid["Alpha/manifest.json"] = "{\"Name\":\"invalid\"}";
    Check(Reject(() => safe.Game.InstallModZipAutomatically(Zip("invalid-manifest", invalid))) && Same(safeBefore, Tree(safe.Mods)), "incomplete manifest cannot enter the active Mods directory");
    var collision = Package("NewBeforeCollision", "Safety.New", "1.0.0");
    foreach (var p in Package("Untouched", "Safety.DifferentIdentity", "1.0.0")) collision.Add(p.Key, p.Value);
    Check(Reject(() => safe.Game.InstallModZipAutomatically(Zip("destination-collision", collision))) && Same(safeBefore, Tree(safe.Mods)), "a later target collision prevents partial installation of an earlier valid new mod");
    var duplicateId = Package("One", "Safety.Duplicate", "1.0.0");
    foreach (var p in Package("Two", "Safety.Duplicate", "1.0.0")) duplicateId.Add(p.Key, p.Value);
    Check(Reject(() => safe.Game.InstallModZipAutomatically(Zip("duplicate-id", duplicateId))) && Same(safeBefore, Tree(safe.Mods)), "duplicate incoming identities reject the entire batch");

    var uncertain = Fixture("uncertain-target");
    Add(uncertain.Game, "First", "Old.Duplicate"); Add(uncertain.Game, "Second", "Old.Duplicate");
    var uncertainBefore = Tree(uncertain.Mods);
    Check(Reject(() => uncertain.Game.InstallModZipAutomatically(Zip("old-duplicate", Package("New", "New.Unrelated", "1.0.0")))) && Same(uncertainBefore, Tree(uncertain.Mods)), "duplicate old identities are rejected rather than guessed or removed");
    var malformed = Fixture("old-invalid");
    var malformedOld = Add(malformed.Game, "Old", "Old.Valid");
    File.WriteAllText(Path.Combine(malformedOld, "manifest.json"), "{\"Name\":\"MissingIdentity\"}");
    var malformedBefore = Tree(malformed.Mods);
    Check(Reject(() => malformed.Game.InstallModZipAutomatically(Zip("old-missing-id", Package("New", "New.Valid", "1.0.0")))) && Same(malformedBefore, Tree(malformed.Mods)), "missing old identity refuses the whole batch without deleting unknown data");

    var cfgCollision = Fixture("config-collision");
    var cfgOld = Add(cfgCollision.Game, "Old", "Config.Old");
    Directory.CreateDirectory(Path.Combine(cfgOld, "user")); File.WriteAllText(Path.Combine(cfgOld, "user", "config.json"), "{\"keep\":true}");
    var cfgBefore = Tree(cfgCollision.Mods);
    var cfgZip = Package("AuthorNew", "Config.Old", "2.0.0"); cfgZip["AuthorNew/user"] = "not-a-directory";
    Check(Reject(() => cfgCollision.Game.InstallModZipAutomatically(Zip("config-path-collision", cfgZip))) && Same(cfgBefore, Tree(cfgCollision.Mods)), "configuration path collision is detected before replacing the old tree");

    var rollback = Fixture("rollback");
    var rollbackFirst = Add(rollback.Game, "First", "Rollback.First");
    var rollbackSecond = Add(rollback.Game, "Second", "Rollback.Second");
    var rollbackBefore = Tree(rollback.Mods);
    var both = Package("FirstNew", "Rollback.First", "2.0.0");
    foreach (var p in Package("SecondNew", "Rollback.Second", "2.0.0")) both.Add(p.Key, p.Value);
    var rollbackZip = Zip("rollback-updates", both);
    // Shared reading permits the complete backup but Windows denies renaming this old tree.
    bool rollbackRejected;
    using (var held = new FileStream(Path.Combine(rollbackSecond, "fixture.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        rollbackRejected = Reject(() => rollback.Game.InstallModZipAutomatically(rollbackZip));
    Check(rollbackRejected && Same(rollbackBefore, Tree(rollback.Mods)) && Directory.Exists(Path.Combine(rollback.Settings, "ModBackups")) && Directory.GetFiles(Path.Combine(rollback.Settings, "ModBackups"), "restore-info.json", SearchOption.AllDirectories).Length == 1, "locked later update rolls back the earlier replacement and retains complete recovery backups");

    var blockedBackup = Fixture("backup-blocked");
    var blockedOld = Add(blockedBackup.Game, "Old", "Backup.Old");
    var blockedBefore = Tree(blockedBackup.Mods);
    bool blockedRejected;
    using (var held = new FileStream(Path.Combine(blockedOld, "fixture.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        blockedRejected = Reject(() => blockedBackup.Game.InstallModZipAutomatically(Zip("backup-blocked", Package("New", "Backup.Old", "2.0.0"))));
    Check(blockedRejected && Same(blockedBefore, Tree(blockedBackup.Mods)), "unreadable backup stops the update before any old directory is moved");

    // Matches the real 3DM AloeMilkfish translation: three data files, no manifest.
    var patchFiles = new Dictionary<string, string>
    {
        ["[CP] AloeMilkfish/code/locations.json"] = "{\"LocationName\":\"汉化地点\"}",
        ["[CP] AloeMilkfish/i18n/default.json"] = "{\"Greeting\":\"你好\"}",
        ["[CP] AloeMilkfish/i18n/zh.json"] = "{\"Greeting\":\"你好\"}"
    };
    var translationZip = Zip("aloe-translation", patchFiles);
    var missingBase = Fixture("translation-missing-base");
    string missingBaseError = "";
    try { missingBase.Game.InstallModZipAutomatically(translationZip); }
    catch (InvalidDataException ex) { missingBaseError = ex.Message; }
    Check(missingBaseError.Contains("这是汉化补丁") && missingBaseError.Contains("需要先安装原版") && missingBaseError.Contains("文件已缓存") && missingBase.Game.GetMods().Count == 0 && !Directory.Exists(Path.Combine(missingBase.Mods, "[CP] AloeMilkfish")), "data-only translation reports missing original mod without creating a fake install");
    var patchGame = Fixture("translation-installed");
    var patchOld = Add(patchGame.Game, ".[CP] AloeMilkfish", "Translation.Aloe", "1.4.0");
    File.WriteAllText(Path.Combine(patchOld, "config.json"), "{\"UserChoice\":\"keep\"}");
    File.WriteAllText(Path.Combine(patchOld, "content.json"), "{\"Original\":true}");
    Directory.CreateDirectory(Path.Combine(patchOld, "i18n")); File.WriteAllText(Path.Combine(patchOld, "i18n", "default.json"), "{\"Greeting\":\"Hi\"}");
    var patchOldTree = Tree(patchOld);
    var patched = patchGame.Game.InstallModZipAutomatically(translationZip);
    Check(patched.PatchedCount == 1 && patched.UpdatedCount == 0 && !Mod(patchGame.Game, "Translation.Aloe").Enabled && Mod(patchGame.Game, "Translation.Aloe").Version == "1.4.0" && File.ReadAllText(Path.Combine(patchOld, "config.json")) == "{\"UserChoice\":\"keep\"}" && Tree(patchOld)["manifest.json"] == patchOldTree["manifest.json"] && Tree(patchOld)["fixture.dll"] == patchOldTree["fixture.dll"] && File.ReadAllText(Path.Combine(patchOld, "i18n", "zh.json")).Contains("你好"), "data translation merges into exact disabled original while preserving manifest, code, version and config");
    Check(Same(patchOldTree, Tree(Directory.GetDirectories(patched.BackupDirectory).Single())), "translation retains a complete recoverable prepatch backup");
    var patchedTree = Tree(patchGame.Mods);
    var repeatPatch = patchGame.Game.InstallModZipAutomatically(translationZip);
    Check(repeatPatch.SkippedCount == 1 && repeatPatch.PatchedCount == 0 && repeatPatch.BackupDirectory == "" && Same(patchedTree, Tree(patchGame.Mods)), "same translation patch is idempotent without another backup");
    var unsafePatch = new Dictionary<string, string>(patchFiles) { ["[CP] AloeMilkfish/new-code.dll"] = "blocked-code" };
    Check(Reject(() => patchGame.Game.InstallModZipAutomatically(Zip("unsafe-data-dll", unsafePatch))) && Same(patchedTree, Tree(patchGame.Mods)), "manifest-free patches cannot install DLL payloads");
    var ambiguousPatch = Fixture("translation-ambiguous");
    Add(ambiguousPatch.Game, Path.Combine("One", "[CP] AloeMilkfish"), "Translation.First");
    Add(ambiguousPatch.Game, Path.Combine("Two", "[CP] AloeMilkfish"), "Translation.Second");
    var ambiguousBefore = Tree(ambiguousPatch.Mods);
    Check(Reject(() => ambiguousPatch.Game.InstallModZipAutomatically(translationZip)) && Same(ambiguousBefore, Tree(ambiguousPatch.Mods)), "duplicate folder-name patch targets are rejected without guessing an identity");
    Console.WriteLine($"PASS {passed} automated-install checks; isolated fixture directories only, no game process or DLL executed.");
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path");
    Directory.Delete(resolved, true);
}
