using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout.Services;

/// <summary>Portable Stardew/SMAPI operations. Never edits a mod's manifest or downloads game files.</summary>
public sealed class StardewService
{
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private const int MaxEntries = 20000;
    private static readonly string[] GameExecutables = { "Stardew Valley.exe", "StardewValley.exe" };
    private static readonly Regex SemanticVersion = new(@"^(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
    private readonly string settingsDirectory;
    private readonly string settingsPath;
    private readonly string savesDirectory;
    private readonly object mutationLock = new();

    public StardewSettings Settings { get; private set; }

    public StardewService(string? settingsDirectory = null, string? savesDirectory = null)
    {
        this.settingsDirectory = FullPath(settingsDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL", "Stardew"));
        settingsPath = Path.Combine(this.settingsDirectory, "launcher-settings.json");
        this.savesDirectory = FullPath(savesDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewValley", "Saves"));
        Settings = new StardewSettings();
        if (File.Exists(settingsPath))
        {
            EnsureNoLinks(settingsPath);
            try
            {
                if (new FileInfo(settingsPath).Length > 1024 * 1024) throw new InvalidDataException("配置文件过大。 ");
                Settings = JsonConvert.DeserializeObject<StardewSettings>(File.ReadAllText(settingsPath, Encoding.UTF8)) ?? throw new InvalidDataException("配置文件为空。");
                Settings.GameDirectory ??= "";
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException)
            {
                throw new InvalidDataException($"星露谷配置损坏，请修复或重命名此文件后重试：{settingsPath}", ex);
            }
        }
    }

    public List<string> DetectGameFolders()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(HashSet<string> set, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try { set.Add(FullPath(value!)); } catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
        }
        Add(candidates, Settings.GameDirectory);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var steam = root.OpenSubKey(@"SOFTWARE\Valve\Steam");
                Add(steamRoots, steam?.GetValue("SteamPath") as string);
                Add(steamRoots, steam?.GetValue("InstallPath") as string);
                using var game = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 413150");
                Add(candidates, game?.GetValue("InstallLocation") as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException || ex is PlatformNotSupportedException) { }
        }
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(programFiles)) continue;
            Add(steamRoots, Path.Combine(programFiles, "Steam"));
            Add(candidates, Path.Combine(programFiles, "GOG Galaxy", "Games", "Stardew Valley"));
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                foreach (var name in new[] { "Steam", "SteamLibrary", @"Games\Steam" }) Add(steamRoots, Path.Combine(drive.RootDirectory.FullName, name));
                foreach (var name in new[] { @"GOG Games\Stardew Valley", @"Games\Stardew Valley", "Stardew Valley" }) Add(candidates, Path.Combine(drive.RootDirectory.FullName, name));
            }
            catch (IOException) { }
        }
        foreach (string steam in steamRoots.ToList())
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                if (new FileInfo(vdf).Length > 4 * 1024 * 1024) continue;
                var content = File.ReadAllText(vdf);
                foreach (Match match in Regex.Matches(content, "\"(?:path|[0-9]+)\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    Add(steamRoots, match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
        foreach (string steam in steamRoots) Add(candidates, Path.Combine(steam, "steamapps", "common", "Stardew Valley"));
        return candidates.Where(HasGameAt).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public GameInstallation GetInstallation()
    {
        var result = new GameInstallation();
        if (string.IsNullOrWhiteSpace(Settings.GameDirectory)) return result;
        result.DirectoryPath = FullPath(Settings.GameDirectory);
        result.HasGame = HasGameAt(result.DirectoryPath);
        result.HasSmapi = File.Exists(Path.Combine(result.DirectoryPath, "StardewModdingAPI.exe"));
        result.GameVersion = ReadVersion(result.DirectoryPath, new[] { "Stardew Valley.dll", "StardewValley.dll", "Stardew Valley.exe", "StardewValley.exe" }, "未识别");
        result.SmapiVersion = ReadVersion(result.DirectoryPath, new[] { "StardewModdingAPI.dll", "StardewModdingAPI.exe" }, result.HasSmapi ? "未识别" : "未安装");
        return result;
    }

    public void SetGameDirectory(string directory)
    {
        var path = FullPath(directory);
        if (!HasGameAt(path)) throw new InvalidOperationException("所选文件夹没有 Stardew Valley.exe。请在 Steam 中浏览游戏本地文件，并选择游戏所在文件夹。");
        lock (mutationLock)
        {
            var previous = Settings.GameDirectory;
            Settings.GameDirectory = path;
            try { SaveSettings(); } catch { Settings.GameDirectory = previous; throw; }
        }
    }

    public void SaveSettings()
    {
        lock (mutationLock)
        {
            EnsureNoLinks(settingsDirectory);
            Directory.CreateDirectory(settingsDirectory);
            EnsureNoLinks(settingsPath);
            var temp = Path.Combine(settingsDirectory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(Settings, Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(settingsPath)) File.Replace(temp, settingsPath, null);
                else File.Move(temp, settingsPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public Process Launch(bool useSmapi)
    {
        var directory = RequireGame();
        string executable = useSmapi ? Path.Combine(directory, "StardewModdingAPI.exe") : GameExecutables.Select(name => Path.Combine(directory, name)).First(File.Exists);
        if (!File.Exists(executable)) throw new InvalidOperationException("未找到 StardewModdingAPI.exe。请先从 smapi.io 安装 SMAPI，或切换为原版启动。");
        EnsureNoLinks(executable);
        Settings.UseSmapi = useSmapi;
        SaveSettings();
        return Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = true }) ?? throw new InvalidOperationException("游戏进程未能创建，请检查游戏安装或 SMAPI 日志。");
    }

    public List<StardewMod> GetMods()
    {
        if (string.IsNullOrWhiteSpace(Settings.GameDirectory)) return new List<StardewMod>();
        var root = Path.Combine(FullPath(Settings.GameDirectory), "Mods");
        if (!Directory.Exists(root)) return new List<StardewMod>();
        EnsureNoLinks(root);
        var mods = ScanMods(root, false);
        var byId = mods.Where(m => m.Valid).GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Where(m => m.Valid))
        {
            var problems = new List<string>();
            if (byId[mod.UniqueId].Count > 1) problems.Add("UniqueID 重复，需移走重复版本");
            foreach (var dependency in mod.Dependencies.Where(d => d.IsRequired))
            {
                if (!byId.TryGetValue(dependency.UniqueId, out var found)) problems.Add("缺少依赖：" + dependency.UniqueId);
                else
                {
                    var enabled = found.Where(m => m.Enabled).ToList();
                    if (enabled.Count == 0) problems.Add("依赖已停用：" + dependency.UniqueId);
                    else if (!string.IsNullOrEmpty(dependency.MinimumVersion) && enabled.All(m => CompareVersions(m.Version, dependency.MinimumVersion) < 0))
                        problems.Add($"依赖版本过低：{dependency.UniqueId} 需要 {dependency.MinimumVersion}");
                }
            }
            mod.Status = string.Join("；", (mod.Enabled ? Enumerable.Empty<string>() : new[] { "已停用" }).Concat(problems));
            if (mod.Status.Length == 0) mod.Status = "可用";
        }
        return mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SetModEnabled(StardewMod mod, bool enabled)
    {
        if (mod == null) throw new ArgumentNullException(nameof(mod));
        lock (mutationLock)
        {
            var root = Path.Combine(RequireGame(), "Mods");
            var source = FullPath(mod.DirectoryPath);
            EnsureWithin(source, root);
            EnsureNoLinks(source);
            var current = GetMods().FirstOrDefault(m => string.Equals(m.DirectoryPath, source, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("此 MOD 已移动或不在当前游戏的 Mods 文件夹内，请刷新列表。");
            if (current.Enabled == enabled) return;
            var parent = Path.GetDirectoryName(source)!;
            if (enabled && RelativePath(root, parent).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.StartsWith(".", StringComparison.Ordinal)))
                throw new InvalidOperationException("此 MOD 所属分组已停用。请先移除分组文件夹名称前的点，再启用 MOD。");
            var name = Path.GetFileName(source);
            var newName = enabled ? name.TrimStart('.') : "." + name;
            if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("MOD 文件夹名称无效，请先重命名文件夹。");
            ValidateSegment(newName);
            var destination = Path.Combine(parent, newName);
            EnsureWithin(destination, root);
            EnsureNoLinks(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("启停目标文件夹已存在，请先移走同名文件夹：" + destination);
            Directory.Move(source, destination);
            mod.DirectoryPath = destination;
            mod.Enabled = enabled;
            mod.Status = enabled ? "已启用，请刷新依赖状态" : "已停用";
        }
    }

    public List<StardewMod> SetModStates(IReadOnlyDictionary<string, bool> requested)
    {
        if (requested.Count > 5000) throw new ArgumentException("一次最多调整 5000 个 Mod。");
        lock (mutationLock)
        {
            var root = Path.Combine(RequireGame(), "Mods");
            var mods = GetMods();
            var plans = new List<(string Source, string Destination)>();
            foreach (var (path, enabled) in requested)
            {
                var source = FullPath(path);
                EnsureWithin(source, root); EnsureNoLinks(source);
                var mod = mods.FirstOrDefault(m => m.DirectoryPath.Equals(source, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Mod 已移动，请刷新列表后重新选择。");
                if (mod.Enabled == enabled) continue;
                var parent = Path.GetDirectoryName(source)!;
                if (enabled && RelativePath(root, parent).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.StartsWith('.')))
                    throw new InvalidOperationException("Mod 所属分组已停用，请先恢复分组文件夹：" + mod.Name);
                var name = enabled ? Path.GetFileName(source).TrimStart('.') : "." + Path.GetFileName(source);
                ValidateSegment(name);
                var destination = Path.Combine(parent, name);
                EnsureWithin(destination, root); EnsureNoLinks(destination);
                if (File.Exists(destination) || Directory.Exists(destination) || plans.Any(p => p.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("启停目标已存在，整个批次未执行：" + name);
                plans.Add((source, destination));
            }
            var moved = new List<(string Source, string Destination)>();
            try
            {
                foreach (var plan in plans)
                {
                    EnsureNoLinks(plan.Source); EnsureNoLinks(plan.Destination);
                    Directory.Move(plan.Source, plan.Destination); moved.Add(plan);
                }
            }
            catch
            {
                foreach (var plan in moved.AsEnumerable().Reverse())
                {
                    EnsureWithin(plan.Source, root); EnsureWithin(plan.Destination, root);
                    EnsureNoLinks(plan.Source); EnsureNoLinks(plan.Destination);
                    if (Directory.Exists(plan.Destination) && !Directory.Exists(plan.Source)) Directory.Move(plan.Destination, plan.Source);
                }
                throw;
            }
            return GetMods();
        }
    }

    public List<StardewMod> InstallModZip(string archivePath)
    {
        lock (mutationLock)
        {
            var archive = FullPath(archivePath);
            if (!File.Exists(archive)) throw new FileNotFoundException("找不到所选 MOD ZIP 文件。", archive);
            EnsureNoLinks(archive);
            if (new FileInfo(archive).Length > MaxArchiveBytes) throw new InvalidDataException("ZIP 超过 512 MB，请解压检查后手动安装。");
            var root = Path.Combine(RequireGame(), "Mods");
            EnsureNoLinks(root);
            Directory.CreateDirectory(root);
            var stage = Path.Combine(root, ".pcl-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var moved = new List<(string Source, string Destination)>();
            try
            {
                ExtractChecked(archive, stage);
                var incoming = ScanMods(stage, true);
                if (incoming.Count == 0) throw new InvalidDataException("ZIP 中没有 manifest.json。请选择 SMAPI MOD 的完整 ZIP 文件。");
                var bad = incoming.FirstOrDefault(m => !m.Valid);
                if (bad != null) throw new InvalidDataException($"无法安装 {bad.Name}：{bad.Status}");
                var duplicate = incoming.GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (duplicate != null) throw new InvalidDataException("ZIP 内包含相同 UniqueID 的多个 MOD，请拆分安装：" + duplicate.Key);
                var existing = GetMods();
                var plans = new List<(string Source, string Destination)>();
                foreach (var mod in incoming)
                {
                    if (!mod.Enabled) throw new InvalidDataException("ZIP 的 MOD 位于点开头的停用文件夹内，请检查压缩包结构：" + mod.Name);
                    var previous = existing.FirstOrDefault(m => string.Equals(m.UniqueId, mod.UniqueId, StringComparison.OrdinalIgnoreCase));
                    if (previous != null) throw new IOException($"已安装 {previous.Name}（{previous.Version}）。请先备份并移走旧 MOD 文件夹后重新安装；不会覆盖原文件：{previous.DirectoryPath}");
                    var folder = string.Equals(mod.DirectoryPath, stage, StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(archive) : Path.GetFileName(mod.DirectoryPath);
                    ValidateSegment(folder);
                    var destination = Path.Combine(root, folder);
                    EnsureWithin(destination, root);
                    EnsureNoLinks(destination);
                    if (Directory.Exists(destination) || File.Exists(destination) || plans.Any(p => string.Equals(p.Destination, destination, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("同名 MOD 文件夹已存在，请先备份并移走旧文件夹；不会覆盖原文件：" + destination);
                    plans.Add((mod.DirectoryPath, destination));
                }
                // Finish all validation before exposing any new mod to SMAPI.
                foreach (var plan in plans)
                {
                    EnsureNoLinks(plan.Source);
                    EnsureNoLinks(plan.Destination);
                    Directory.Move(plan.Source, plan.Destination);
                    moved.Add(plan);
                }
                return GetMods().Where(m => plans.Any(p => string.Equals(p.Destination, m.DirectoryPath, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            catch
            {
                for (int i = moved.Count - 1; i >= 0; i--)
                {
                    var plan = moved[i];
                    try
                    {
                        EnsureNoLinks(plan.Destination);
                        EnsureNoLinks(plan.Source);
                        if (Directory.Exists(plan.Destination) && !Directory.Exists(plan.Source)) Directory.Move(plan.Destination, plan.Source);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                throw;
            }
            finally
            {
                if (Directory.Exists(stage))
                {
                    try
                    {
                        EnsureWithin(stage, root);
                        EnsureNoLinks(stage);
                        VerifyTree(stage);
                        Directory.Delete(stage, true);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
        }
    }

    /// <summary>Install a downloaded mod batch, safely replacing older identities and preserving their configuration.</summary>
    public ModInstallResult InstallModZipAutomatically(string archivePath)
    {
        lock (mutationLock)
        {
            var archive = FullPath(archivePath);
            if (!File.Exists(archive)) throw new FileNotFoundException("找不到 MOD ZIP 文件。", archive);
            EnsureNoLinks(archive);
            if (new FileInfo(archive).Length > MaxArchiveBytes) throw new InvalidDataException("ZIP 超过 512 MB，无法自动安装。");
            var root = Path.Combine(RequireGame(), "Mods");
            EnsureNoLinks(root);
            Directory.CreateDirectory(root);
            var stage = Path.Combine(root, ".pcl-import-" + Guid.NewGuid().ToString("N"));
            EnsureWithin(stage, root);
            EnsureNoLinks(stage);
            Directory.CreateDirectory(stage);
            var extracted = Path.Combine(stage, "extracted");
            Directory.CreateDirectory(extracted);
            var result = new ModInstallResult();
            var plans = new List<AutomaticInstallPlan>();
            var keepStageForRecovery = false;
            try
            {
                ExtractChecked(archive, extracted);
                var incoming = ScanMods(extracted, true);
                var bad = incoming.FirstOrDefault(m => !m.Valid);
                if (bad != null) throw new InvalidDataException($"无法安装 {bad.Name}：{bad.Status}");
                var repeated = incoming.GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (repeated != null) throw new InvalidDataException("ZIP 包含重复 UniqueID，整个批次未安装：" + repeated.Key);
                if (incoming.Any(m => !m.Enabled)) throw new InvalidDataException("ZIP 含点开头的停用 MOD 文件夹，请检查压缩包结构。");

                var existing = GetMods();
                bad = existing.FirstOrDefault(m => !m.Valid);
                if (bad != null) throw new InvalidDataException($"已安装 MOD 的清单无效，无法安全判定更新目标：{bad.Name}（{bad.Status}）。整个批次未安装。");
                repeated = existing.GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (repeated != null) throw new InvalidDataException("已安装 MOD 的 UniqueID 重复，无法安全判定更新目标。整个批次未安装：" + repeated.Key);
                var byId = existing.ToDictionary(m => m.UniqueId, StringComparer.OrdinalIgnoreCase);
                var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (incoming.Count == 0) plans.AddRange(PrepareDataPatchPlans(extracted, existing, root, stage, result));
                foreach (var mod in incoming)
                {
                    EnsureWithin(mod.DirectoryPath, stage);
                    EnsureNoLinks(mod.DirectoryPath);
                    VerifyTree(mod.DirectoryPath);
                    byId.TryGetValue(mod.UniqueId, out var previous);
                    if (previous != null)
                    {
                        var comparison = CompareVersions(previous.Version, mod.Version);
                        if (comparison >= 0)
                        {
                            result.SkippedCount++;
                            result.Warnings.Add(comparison > 0
                                ? $"已保留 {previous.Name} {previous.Version}：下载版本 {mod.Version} 较旧，未降级。"
                                : $"{previous.Name} {previous.Version} 已安装：相同版本保留现有文件与配置，未重复覆盖。");
                            continue;
                        }
                    }
                    var folder = mod.DirectoryPath.Equals(extracted, StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileNameWithoutExtension(archive) : Path.GetFileName(mod.DirectoryPath);
                    ValidateSegment(folder);
                    var destination = previous?.DirectoryPath ?? Path.Combine(root, folder);
                    EnsureWithin(destination, root);
                    EnsureNoLinks(destination);
                    if (!destinations.Add(destination) || previous == null && (Directory.Exists(destination) || File.Exists(destination)))
                        throw new IOException("目标文件夹已存在或多个 MOD 使用同一目标，整个批次未安装：" + destination);
                    var plan = new AutomaticInstallPlan
                    {
                        Incoming = mod, Previous = previous, Destination = destination,
                        RollbackDirectory = Path.Combine(stage, "previous-" + plans.Count.ToString(CultureInfo.InvariantCulture))
                    };
                    if (previous != null)
                    {
                        EnsureWithin(previous.DirectoryPath, root);
                        var files = VerifyTree(previous.DirectoryPath);
                        if (files.Sum(f => new FileInfo(f).Length) > 2L * MaxExpandedBytes)
                            throw new IOException("旧 MOD 超过 2 GB，无法自动备份。整个批次未安装：" + previous.Name);
                        // Only user configuration is carried forward; obsolete DLLs and assets stay in the backup.
                        foreach (var config in files.Where(f => Path.GetFileName(f).Equals("config.json", StringComparison.OrdinalIgnoreCase)))
                        {
                            var target = SafeEntryPath(mod.DirectoryPath, RelativePath(previous.DirectoryPath, config));
                            EnsureWithin(target, stage);
                            if (new FileInfo(config).Length > MaxEntryBytes) throw new IOException("MOD 配置超过 256 MB，无法安全保留。");
                            EnsureNoLinks(config); EnsureNoLinks(target);
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Copy(config, target, true);
                        }
                    }
                    plans.Add(plan);
                }

                var updates = plans.Where(p => p.Previous != null).ToList();
                if (updates.Count > 0)
                {
                    var backupRoot = Path.Combine(settingsDirectory, "ModBackups");
                    if (IsWithin(backupRoot, root, true)) throw new InvalidOperationException("启动器配置目录不能位于 Mods 文件夹内，否则无法安全备份更新。");
                    EnsureNoLinks(backupRoot);
                    var backupDirectory = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                    EnsureWithin(backupDirectory, backupRoot);
                    EnsureNoLinks(backupDirectory);
                    Directory.CreateDirectory(backupDirectory);
                    result.BackupDirectory = backupDirectory;
                    for (var i = 0; i < updates.Count; i++)
                    {
                        var plan = updates[i];
                        plan.BackupDirectory = Path.Combine(backupDirectory, i.ToString("D4", CultureInfo.InvariantCulture) + "-" + Path.GetFileName(plan.Destination));
                        CopyModTreeVerified(plan.Destination, plan.BackupDirectory, root, backupDirectory);
                    }
                    var recovery = updates.Select(p => new
                    {
                        p.Previous!.UniqueId, OriginalDirectory = p.Destination,
                        BackupFolder = Path.GetFileName(p.BackupDirectory),
                        OriginalVersion = p.Previous.Version, NewVersion = p.Incoming.Version,
                        p.Previous.Enabled
                    });
                    File.WriteAllText(Path.Combine(backupDirectory, "restore-info.json"), JsonConvert.SerializeObject(recovery, Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
                }

                // Every archive, target and complete old-tree backup is validated before the first replacement.
                try
                {
                    foreach (var plan in plans)
                    {
                        EnsureWithin(plan.Destination, root); EnsureWithin(plan.Incoming.DirectoryPath, stage);
                        EnsureNoLinks(plan.Destination); EnsureNoLinks(plan.Incoming.DirectoryPath);
                        if (plan.Previous != null)
                        {
                            EnsureWithin(plan.RollbackDirectory, stage); EnsureNoLinks(plan.RollbackDirectory);
                            Directory.Move(plan.Destination, plan.RollbackDirectory);
                            plan.OriginalMoved = true;
                        }
                        else if (Directory.Exists(plan.Destination) || File.Exists(plan.Destination))
                            throw new IOException("安装期间目标目录已被其他程序创建：" + plan.Destination);
                        Directory.Move(plan.Incoming.DirectoryPath, plan.Destination);
                        plan.IncomingMoved = true;
                    }
                    result.Mods = GetMods();
                }
                catch (Exception installError)
                {
                    var rollbackErrors = new List<Exception>();
                    foreach (var plan in plans.AsEnumerable().Reverse())
                    {
                        try
                        {
                            EnsureWithin(plan.Destination, root); EnsureWithin(plan.Incoming.DirectoryPath, stage);
                            EnsureNoLinks(plan.Destination); EnsureNoLinks(plan.Incoming.DirectoryPath);
                            if (plan.IncomingMoved)
                            {
                                Directory.Move(plan.Destination, plan.Incoming.DirectoryPath);
                                plan.IncomingMoved = false;
                            }
                            if (plan.OriginalMoved)
                            {
                                EnsureWithin(plan.RollbackDirectory, stage); EnsureNoLinks(plan.RollbackDirectory);
                                Directory.Move(plan.RollbackDirectory, plan.Destination);
                                plan.OriginalMoved = false;
                            }
                        }
                        catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                        {
                            rollbackErrors.Add(rollbackError);
                        }
                    }
                    if (rollbackErrors.Count > 0)
                    {
                        keepStageForRecovery = true;
                        throw new IOException($"安装未完成，部分回滚被其他程序占用阻止。完整旧版本备份：{result.BackupDirectory}；临时恢复目录：{stage}", new AggregateException(new[] { installError }.Concat(rollbackErrors)));
                    }
                    throw new IOException("安装未完成，整个批次已恢复原状。旧版本备份已保留：" + result.BackupDirectory, installError);
                }
                result.InstalledCount = plans.Count(p => p.Previous == null);
                result.UpdatedCount = updates.Count(p => !p.IsPatch);
                result.PatchedCount = updates.Count(p => p.IsPatch);
                return result;
            }
            finally
            {
                if (!keepStageForRecovery && Directory.Exists(stage))
                {
                    try
                    {
                        EnsureWithin(stage, root); EnsureNoLinks(stage); VerifyTree(stage);
                        Directory.Delete(stage, true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    public ModRemovalResult RemoveMod(string directoryPath)
    {
        lock (mutationLock)
        {
            var gameRoot = RequireGame();
            var modsRoot = Path.Combine(gameRoot, "Mods");
            var source = FullPath(directoryPath);
            EnsureWithin(source, modsRoot); EnsureNoLinks(source);
            var mod = GetMods().SingleOrDefault(m => m.DirectoryPath.Equals(source, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("此目录不是当前游戏列表中的 Mod，请刷新后重试。");
            var removedRoot = Path.Combine(settingsDirectory, "RemovedMods");
            if (IsWithin(removedRoot, modsRoot, true)) throw new InvalidOperationException("删除备份目录不能位于 Mods 内。");
            EnsureNoLinks(removedRoot);
            var receipt = Path.Combine(removedRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(receipt, Path.GetFileName(source));
            CopyModTreeVerified(source, backup, modsRoot, receipt);
            File.WriteAllText(Path.Combine(receipt, "restore-info.json"), JsonConvert.SerializeObject(new
            { mod.Name, mod.UniqueId, OriginalDirectory = source, BackupFolder = Path.GetFileName(backup), mod.Enabled }, Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
            // Moving within the game volume removes the mod atomically; verified copies also work when launcher data is on another drive.
            var retired = Path.Combine(gameRoot, ".starsprout-removed-" + Guid.NewGuid().ToString("N"));
            EnsureWithin(retired, gameRoot); EnsureNoLinks(retired);
            Directory.Move(source, retired);
            var result = new ModRemovalResult { Name = mod.Name, UniqueId = mod.UniqueId, BackupDirectory = backup };
            try { VerifyTree(retired); Directory.Delete(retired, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.CleanupPending = true; }
            return result;
        }
    }

    private sealed class AutomaticInstallPlan
    {
        public StardewMod Incoming { get; set; } = new();
        public StardewMod? Previous { get; set; }
        public string Destination { get; set; } = "";
        public string RollbackDirectory { get; set; } = "";
        public string BackupDirectory { get; set; } = "";
        public bool OriginalMoved { get; set; }
        public bool IncomingMoved { get; set; }
        public bool IsPatch { get; set; }
    }

    private static List<AutomaticInstallPlan> PrepareDataPatchPlans(string extracted, List<StardewMod> existing, string root, string stage, ModInstallResult result)
    {
        var files = VerifyTree(extracted);
        if (files.Count == 0) throw new InvalidDataException("ZIP 中没有 MOD 清单或可安装的数据文件。");
        var allowedExtensions = new HashSet<string>(new[] { ".json", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".txt", ".tmx", ".tsx", ".tbin", ".xnb" }, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relative = RelativePath(extracted, file);
            if (relative.Split(Path.DirectorySeparatorChar).Length < 2 || !allowedExtensions.Contains(Path.GetExtension(file)))
                throw new InvalidDataException("无 manifest 的汉化补丁仅允许一级 MOD 文件夹内的数据文件，禁止 DLL、EXE、脚本或根目录文件；文件已缓存：" + relative);
            if (Path.GetFileName(file).Equals("config.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("汉化补丁不能覆盖用户 config.json；文件已缓存。请选择仅含汉化数据的补丁。");
        }
        var plans = new List<AutomaticInstallPlan>();
        foreach (var patchFolder in Directory.EnumerateDirectories(extracted))
        {
            var patchFiles = files.Where(f => IsWithin(f, patchFolder)).ToList();
            if (patchFiles.Count == 0) continue;
            var folderName = Path.GetFileName(patchFolder).TrimStart('.');
            var matches = existing.Where(m => Path.GetFileName(m.DirectoryPath).TrimStart('.').Equals(folderName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                throw new InvalidDataException($"这是汉化补丁，需要先安装原版 MOD：{folderName}；文件已缓存，安装原版后可直接重试。");
            if (matches.Count > 1) throw new InvalidDataException("汉化补丁的原版目录存在多个匹配项，整个批次未安装；文件已缓存：" + folderName);
            var previous = matches[0];
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(previous.DirectoryPath, "manifest.json"), Encoding.UTF8));
            var entryDll = manifest.GetValue("EntryDll", StringComparison.OrdinalIgnoreCase)?.Value<string>();
            if (!string.IsNullOrWhiteSpace(entryDll))
            {
                var entryPath = SafeEntryPath(previous.DirectoryPath, entryDll!);
                foreach (var file in patchFiles)
                    if (SafeEntryPath(previous.DirectoryPath, RelativePath(patchFolder, file)).Equals(entryPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("数据补丁不能覆盖原版 MOD 的 EntryDll；文件已缓存：" + folderName);
            }
            var identical = patchFiles.All(file =>
            {
                var target = SafeEntryPath(previous.DirectoryPath, RelativePath(patchFolder, file));
                EnsureWithin(target, root); EnsureNoLinks(target);
                if (!File.Exists(target)) return false;
                using var original = File.OpenRead(target);
                using var patch = File.OpenRead(file);
                return SHA256.HashData(original).SequenceEqual(SHA256.HashData(patch));
            });
            if (identical)
            {
                result.SkippedCount++;
                result.Warnings.Add(folderName + " 的补丁数据已相同，未重复覆盖或备份。");
                continue;
            }
            var work = Path.Combine(stage, "patch-" + plans.Count.ToString(CultureInfo.InvariantCulture));
            CopyModTreeVerified(previous.DirectoryPath, work, root, stage);
            foreach (var file in patchFiles)
            {
                EnsureWithin(file, extracted); EnsureNoLinks(file);
                var target = SafeEntryPath(work, RelativePath(patchFolder, file));
                EnsureWithin(target, stage); EnsureNoLinks(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            var incoming = ReadManifest(work, Path.Combine(work, "manifest.json"), previous.Enabled);
            if (!incoming.Valid || !incoming.UniqueId.Equals(previous.UniqueId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("汉化补丁不能改变原版 MOD 的清单或身份；文件已缓存。");
            plans.Add(new AutomaticInstallPlan
            {
                Previous = previous, Incoming = incoming, Destination = previous.DirectoryPath,
                RollbackDirectory = Path.Combine(stage, "previous-" + plans.Count.ToString(CultureInfo.InvariantCulture)), IsPatch = true
            });
        }
        if (plans.Count == 0 && result.SkippedCount == 0) throw new InvalidDataException("ZIP 中没有可安装的汉化补丁数据；文件已缓存。");
        return plans;
    }

    private static void CopyModTreeVerified(string source, string destination, string sourceRoot, string backupRoot)
    {
        EnsureWithin(source, sourceRoot); EnsureWithin(destination, backupRoot);
        EnsureNoLinks(source); EnsureNoLinks(destination);
        var files = VerifyTree(source);
        if (files.Sum(f => new FileInfo(f).Length) > 2L * MaxExpandedBytes) throw new IOException("MOD 超过 2 GB，无法自动备份。");
        Directory.CreateDirectory(destination);
        foreach (var folder in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            EnsureWithin(folder, sourceRoot); EnsureNoLinks(folder);
            var target = SafeEntryPath(destination, RelativePath(source, folder));
            EnsureWithin(target, backupRoot); EnsureNoLinks(target);
            Directory.CreateDirectory(target);
        }
        foreach (var file in files)
        {
            EnsureWithin(file, sourceRoot); EnsureNoLinks(file);
            var target = SafeEntryPath(destination, RelativePath(source, file));
            EnsureWithin(target, backupRoot); EnsureNoLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            input.CopyTo(output);
            output.Flush(true);
            input.Position = 0; output.Position = 0;
            if (!SHA256.HashData(input).SequenceEqual(SHA256.HashData(output))) throw new IOException("MOD 备份完整性检查失败，旧版本未修改。");
        }
    }

    public List<StardewSave> GetSaves()
    {
        var result = new List<StardewSave>();
        if (!Directory.Exists(savesDirectory)) return result;
        EnsureNoLinks(savesDirectory);
        foreach (var folder in Directory.EnumerateDirectories(savesDirectory))
        {
            EnsureNoLinks(folder);
            var info = Path.Combine(folder, "SaveGameInfo");
            if (!File.Exists(info)) continue;
            EnsureNoLinks(info);
            var name = Path.GetFileName(folder);
            try
            {
                using var reader = XmlReader.Create(info, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "name") continue;
                    var farmer = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(farmer)) name = farmer;
                    break;
                }
            }
            catch (XmlException) { }
            var main = Path.Combine(folder, Path.GetFileName(folder));
            EnsureNoLinks(main);
            result.Add(new StardewSave { Name = name, DirectoryPath = FullPath(folder), LastModified = File.Exists(main) ? File.GetLastWriteTime(main) : File.GetLastWriteTime(info) });
        }
        return result.OrderByDescending(s => s.LastModified).ToList();
    }

    public string BackupSave(StardewSave save)
    {
        if (save == null) throw new ArgumentNullException(nameof(save));
        lock (mutationLock)
        {
            var source = FullPath(save.DirectoryPath);
            EnsureWithin(source, savesDirectory);
            if (!string.Equals(FullPath(Path.GetDirectoryName(source)!), savesDirectory, StringComparison.OrdinalIgnoreCase) || !GetSaves().Any(s => string.Equals(s.DirectoryPath, source, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("存档不在当前存档目录中，或 SaveGameInfo 已丢失，请刷新列表。");
            var backupDirectory = Path.Combine(settingsDirectory, "Backups");
            if (IsWithin(backupDirectory, savesDirectory, true)) throw new InvalidOperationException("备份目录不能位于游戏存档目录内，请将启动器配置目录移到其他位置。");
            EnsureNoLinks(backupDirectory);
            Directory.CreateDirectory(backupDirectory);
            var destination = Path.Combine(backupDirectory, Path.GetFileName(source) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            var temporary = destination + ".tmp";
            try
            {
                var files = VerifyTree(source);
                if (files.Count > MaxEntries || files.Sum(f => new FileInfo(f).Length) > 2L * MaxExpandedBytes) throw new IOException("存档超过备份限制（2 GB 或 20000 个文件），请手动备份。");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var file in files)
                    {
                        EnsureNoLinks(file);
                        var entry = zip.CreateEntry(Path.GetFileName(source) + "/" + RelativePath(source, file).Replace('\\', '/'), CompressionLevel.Optimal);
                        var date = File.GetLastWriteTime(file);
                        if (date.Year >= 1980 && date.Year <= 2107) entry.LastWriteTime = new DateTimeOffset(date);
                        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var output = entry.Open();
                        input.CopyTo(output);
                    }
                }
                File.Move(temporary, destination);
                return destination;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public string GetRecentLog()
    {
        var logs = Path.Combine(Path.GetDirectoryName(savesDirectory)!, "ErrorLogs");
        if (!Directory.Exists(logs)) return "暂无 SMAPI 日志。使用 SMAPI 启动游戏后，日志会显示在这里。";
        EnsureNoLinks(logs);
        var latest = Directory.EnumerateFiles(logs, "SMAPI-*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (latest == null) return "暂无 SMAPI 日志。使用 SMAPI 启动游戏后，日志会显示在这里。";
        EnsureNoLinks(latest);
        using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int maxBytes = 512 * 1024;
        var truncated = stream.Length > maxBytes;
        if (truncated) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        if (truncated) reader.ReadLine();
        return (truncated ? "（日志较长，仅显示最后 512 KB）" + Environment.NewLine : "") + reader.ReadToEnd();
    }

    /// <summary>Diagnostic input includes startup errors as well as recent events, within a fixed memory bound.</summary>
    public string GetDiagnosticLog()
    {
        var logs = Path.Combine(Path.GetDirectoryName(savesDirectory)!, "ErrorLogs");
        if (!Directory.Exists(logs)) return "暂无 SMAPI 日志。";
        EnsureNoLinks(logs);
        var latest = Directory.EnumerateFiles(logs, "SMAPI-*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (latest == null) return "暂无 SMAPI 日志。";
        EnsureNoLinks(latest);
        using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int limit = 8 * 1024 * 1024;
        var length = stream.Length;
        string Segment(long start, int count)
        {
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk == 0) break;
                read += chunk;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (start > 0 && text.IndexOf('\n') is var line && line >= 0) text = text[(line + 1)..];
            return text.TrimStart('\uFEFF');
        }
        if (length <= limit) return Segment(0, (int)length);
        return Segment(0, limit / 2) + "\n（日志超过 8 MiB，中间部分已省略；保留开头与结尾）\n" + Segment(length - limit / 2, limit / 2);
    }

    private string RequireGame()
    {
        if (string.IsNullOrWhiteSpace(Settings.GameDirectory)) throw new InvalidOperationException("请先选择星露谷游戏文件夹。");
        var path = FullPath(Settings.GameDirectory);
        if (!HasGameAt(path)) throw new InvalidOperationException("游戏文件夹中未找到 Stardew Valley.exe，请重新选择游戏目录。");
        EnsureNoLinks(path);
        return path;
    }

    private static bool HasGameAt(string directory) => GameExecutables.Any(name => File.Exists(Path.Combine(directory, name)));

    private static string ReadVersion(string directory, IEnumerable<string> names, string fallback)
    {
        foreach (string name in names)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var value = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    // File metadata may append a build hash or comma-separated build fields.
                    var version = Regex.Match(value!, @"(?<!\d)\d+\.\d+(?:\.\d+){0,2}(?:-[0-9A-Za-z.-]+)?", RegexOptions.CultureInvariant);
                    if (version.Success) return version.Value;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception) { }
        }
        return fallback;
    }

    private static List<StardewMod> ScanMods(string root, bool includeRoot)
    {
        var result = new List<StardewMod>();
        var visited = 0;
        void Visit(string folder, int depth, bool disabled)
        {
            if (++visited > MaxEntries || depth > 32) throw new InvalidDataException("MOD 文件夹层级过深或文件夹过多，请检查目录结构。");
            EnsureNoLinks(folder);
            disabled |= !string.Equals(folder, root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(folder).StartsWith(".", StringComparison.Ordinal);
            var manifest = Path.Combine(folder, "manifest.json");
            if (File.Exists(manifest))
            {
                EnsureNoLinks(manifest);
                result.Add(ReadManifest(folder, manifest, !disabled));
                return; // A manifest owns its subtree: nested assets are not additional mods.
            }
            foreach (var child in Directory.EnumerateDirectories(folder)) Visit(child, depth + 1, disabled);
        }
        if (includeRoot) Visit(root, 0, false);
        else foreach (var child in Directory.EnumerateDirectories(root))
        {
            // Staged imports are private work in progress, not installed mods.
            if (Path.GetFileName(child).StartsWith(".pcl-import-", StringComparison.OrdinalIgnoreCase)) continue;
            Visit(child, 1, false);
        }
        return result;
    }

    private static StardewMod ReadManifest(string folder, string manifest, bool enabled)
    {
        var mod = new StardewMod { Name = Path.GetFileName(folder).TrimStart('.'), DirectoryPath = FullPath(folder), Enabled = enabled };
        try
        {
            if (new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException("manifest.json 超过 1 MB");
            var json = JObject.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            mod.Name = RequiredString(json, "Name");
            mod.UniqueId = RequiredString(json, "UniqueID");
            mod.Version = RequiredString(json, "Version");
            mod.Author = RequiredString(json, "Author");
            RequiredString(json, "Description");
            if (!SemanticVersion.IsMatch(mod.Version)) throw new InvalidDataException("Version 不是有效版本号");
            if (mod.UniqueId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new InvalidDataException("UniqueID 包含空格或控制字符");
            var entry = json.GetValue("EntryDll", StringComparison.OrdinalIgnoreCase);
            var pack = json.GetValue("ContentPackFor", StringComparison.OrdinalIgnoreCase);
            if (entry != null && entry.Type != JTokenType.Null && pack != null && pack.Type != JTokenType.Null) throw new InvalidDataException("EntryDll 与 ContentPackFor 不能同时设置");
            if (pack is JObject packInfo) mod.Dependencies.Add(ParseDependency(packInfo, true));
            else if (entry != null && entry.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)entry))
            {
                var dll = SafeEntryPath(folder, (string)entry!);
                if (!File.Exists(dll)) throw new InvalidDataException("EntryDll 指定的文件不存在：" + (string)entry!);
                EnsureNoLinks(dll);
            }
            else throw new InvalidDataException("缺少有效的 EntryDll 或 ContentPackFor");
            var dependencies = json.GetValue("Dependencies", StringComparison.OrdinalIgnoreCase);
            if (dependencies != null && dependencies.Type != JTokenType.Null)
            {
                if (dependencies is not JArray array) throw new InvalidDataException("Dependencies 必须是数组");
                foreach (var value in array)
                {
                    if (value is not JObject dependency) throw new InvalidDataException("Dependencies 包含无效记录");
                    mod.Dependencies.Add(ParseDependency(dependency, false));
                }
            }
            mod.Valid = true;
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is ArgumentException || ex is IOException)
        {
            mod.Valid = false;
            mod.Status = (enabled ? "" : "已停用；") + "清单无效：" + ex.Message;
        }
        return mod;
    }

    private static ModDependency ParseDependency(JObject json, bool contentPack)
    {
        var dependency = new ModDependency { UniqueId = RequiredString(json, "UniqueID") };
        var minimum = json.GetValue("MinimumVersion", StringComparison.OrdinalIgnoreCase);
        if (minimum != null && minimum.Type != JTokenType.Null)
        {
            if (minimum.Type != JTokenType.String || !SemanticVersion.IsMatch((string)minimum!)) throw new InvalidDataException("依赖 MinimumVersion 无效");
            dependency.MinimumVersion = (string)minimum!;
        }
        var required = json.GetValue("IsRequired", StringComparison.OrdinalIgnoreCase);
        if (!contentPack && required != null && required.Type != JTokenType.Null)
        {
            if (required.Type != JTokenType.Boolean) throw new InvalidDataException("依赖 IsRequired 必须为 true 或 false");
            dependency.IsRequired = (bool)required;
        }
        return dependency;
    }

    private static string RequiredString(JObject json, string property)
    {
        var value = json.GetValue(property, StringComparison.OrdinalIgnoreCase);
        if (value == null || value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)value)) throw new InvalidDataException("缺少有效的 " + property);
        return ((string)value!).Trim();
    }

    private static int CompareVersions(string left, string right)
    {
        var a = SemanticVersion.Match(left);
        var b = SemanticVersion.Match(right);
        if (!a.Success || !b.Success) return -1;
        for (var i = 1; i <= 4; i++)
        {
            if (!BigInteger.TryParse(a.Groups[i].Success ? a.Groups[i].Value : "0", NumberStyles.None, CultureInfo.InvariantCulture, out var av) || !BigInteger.TryParse(b.Groups[i].Success ? b.Groups[i].Value : "0", NumberStyles.None, CultureInfo.InvariantCulture, out var bv)) return -1;
            var comparison = av.CompareTo(bv);
            if (comparison != 0) return comparison;
        }
        var ap = a.Groups[5].Value;
        var bp = b.Groups[5].Value;
        if (ap.Length == 0 || bp.Length == 0) return ap.Length == bp.Length ? 0 : ap.Length == 0 ? 1 : -1;
        var aa = ap.Split('.');
        var bb = bp.Split('.');
        for (int i = 0; i < Math.Min(aa.Length, bb.Length); i++)
        {
            var an = BigInteger.TryParse(aa[i], NumberStyles.None, CultureInfo.InvariantCulture, out var av);
            var bn = BigInteger.TryParse(bb[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bv);
            var comparison = an && bn ? av.CompareTo(bv) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(aa[i], bb[i]);
            if (comparison != 0) return comparison;
        }
        return aa.Length.CompareTo(bb.Length);
    }

    private static void ExtractChecked(string archive, string destination)
    {
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("ZIP 文件过多，最多允许 20000 项。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        foreach (var entry in zip.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP 含符号链接或重解析点，不能安全安装：" + entry.FullName);
            var target = SafeEntryPath(destination, entry.FullName);
            if (!paths.Add(target)) throw new InvalidDataException("ZIP 含重复或大小写冲突的文件路径：" + entry.FullName);
            if (entry.Length < 0 || entry.Length > MaxEntryBytes || declaredBytes > MaxExpandedBytes - entry.Length)
                throw new InvalidDataException("ZIP 解压后过大（单文件 256 MB，总计 1 GB）。");
            declaredBytes += entry.Length;
        }
        long written = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            var target = SafeEntryPath(destination, entry.FullName);
            EnsureNoLinks(target);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                if (entry.Length != 0) throw new InvalidDataException("ZIP 的目录项含文件数据：" + entry.FullName);
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            EnsureNoLinks(target);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long entryWritten = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                entryWritten += read;
                written += read;
                if (entryWritten > MaxEntryBytes || written > MaxExpandedBytes || entryWritten > entry.Length) throw new InvalidDataException("ZIP 的实际解压数据超过安全限制。");
                output.Write(buffer, 0, read);
            }
            if (entryWritten != entry.Length) throw new InvalidDataException("ZIP 文件数据不完整：" + entry.FullName);
        }
    }

    private static string SafeEntryPath(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || Path.IsPathRooted(entryName) || entryName.StartsWith("/", StringComparison.Ordinal) || entryName.StartsWith("\\", StringComparison.Ordinal)) throw new InvalidDataException("ZIP 或清单含绝对文件路径。");
        var normalized = entryName.Replace('\\', '/').TrimEnd('/');
        var segments = normalized.Split('/');
        foreach (var segment in segments) ValidateSegment(segment);
        var target = FullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar.ToString(), segments)));
        EnsureWithin(target, root);
        return target;
    }

    private static void ValidateSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) || segment.Any(c => c < 32 || "<>:\"|?*/\\".IndexOf(c) >= 0))
            throw new InvalidDataException("ZIP 或清单含不安全的文件名称：" + segment);
        if (Regex.IsMatch(segment.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("ZIP 或清单含 Windows 保留文件名称：" + segment);
    }

    private static List<string> VerifyTree(string root)
    {
        var files = new List<string>();
        int directories = 0;
        void Visit(string folder, int depth)
        {
            EnsureNoLinks(folder);
            if (depth > 64 || ++directories > MaxEntries) throw new IOException("目录过深或文件夹过多，无法安全处理。");
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                EnsureNoLinks(file);
                files.Add(file);
                if (files.Count > MaxEntries) throw new IOException("文件数量超过 20000，无法安全处理。");
            }
            foreach (var directory in Directory.EnumerateDirectories(folder)) Visit(directory, depth + 1);
        }
        Visit(root, 0);
        return files;
    }

    private static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("文件夹路径为空。");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsWithin(string path, string root, bool allowRoot = false)
    {
        path = FullPath(path);
        root = FullPath(root);
        return (allowRoot && string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureWithin(string path, string root)
    {
        if (!IsWithin(path, root)) throw new InvalidDataException("拒绝处理目标目录以外的文件路径：" + path);
    }

    private static string RelativePath(string root, string path)
    {
        if (string.Equals(FullPath(root), FullPath(path), StringComparison.OrdinalIgnoreCase)) return "";
        EnsureWithin(path, root);
        return FullPath(path).Substring(FullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1);
    }

    private static void EnsureNoLinks(string path)
    {
        var current = FullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                // GetAttributes also detects dangling links, which Exists can hide.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("此路径含符号链接或目录联接，为保护文件请使用真实目录：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}
