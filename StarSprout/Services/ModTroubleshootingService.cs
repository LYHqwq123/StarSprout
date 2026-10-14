using System.IO;
using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace StarSprout.Services;

public sealed class ModTroubleshootingView
{
    public bool CanRestore { get; set; }
    public int DisabledCount { get; set; }
}

/// <summary>Preserves the initial enabled combination while a user isolates a mod conflict.</summary>
public sealed class ModTroubleshootingService
{
    private readonly StardewService game;
    private readonly string directory;
    private string path => Path.Combine(directory, "mod-restore-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.IsNullOrWhiteSpace(game.Settings.GameDirectory) ? "unconfigured" : Path.GetFullPath(game.Settings.GameDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..20] + ".json");
    public ModTroubleshootingService(StardewService game, string settingsDirectory)
    { this.game = game; directory = Path.GetFullPath(settingsDirectory); }

    public ModTroubleshootingView GetState() => new()
    {
        CanRestore = Load() is { } saved && saved.GameDirectory.Equals(game.Settings.GameDirectory, StringComparison.OrdinalIgnoreCase),
        DisabledCount = game.GetMods().Count(m => !m.Enabled)
    };

    public void SetEnabled(IEnumerable<string> paths, bool enabled)
    {
        var selection = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => Path.GetFullPath(p), _ => enabled, StringComparer.OrdinalIgnoreCase);
        if (selection.Count == 0) return;
        var mods = game.GetMods();
        if (selection.Keys.Any(p => !mods.Any(m => m.DirectoryPath.Equals(p, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("所选 Mod 已移动，请刷新后重试。");
        if (!mods.Any(m => selection.TryGetValue(m.DirectoryPath, out var desired) && desired != m.Enabled)) return;
        Capture(mods);
        game.SetModStates(selection);
    }

    public void DisableAll() => SetEnabled(game.GetMods().Where(m => m.Enabled).Select(m => m.DirectoryPath), false);

    public void Restore()
    {
        var saved = Load() ?? throw new InvalidOperationException("还没有可恢复的 Mod 组合。");
        if (!saved.GameDirectory.Equals(game.Settings.GameDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("恢复记录属于其他游戏目录，请切回原目录。");
        var root = Path.Combine(game.Settings.GameDirectory, "Mods");
        var current = game.GetMods().GroupBy(m => Identity(root, m.DirectoryPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var changes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in saved.Mods)
        {
            if (!current.TryGetValue(record.Identity, out var found) || found.Count != 1 || found[0].UniqueId != record.UniqueId)
                throw new InvalidOperationException("Mod 目录内容已改变，未恢复组合：" + record.Identity);
            changes.Add(found[0].DirectoryPath, record.Enabled);
        }
        game.SetModStates(changes);
        NoLinks(path); File.Delete(path);
    }

    private void Capture(List<StardewMod> mods)
    {
        var old = Load();
        if (old?.GameDirectory.Equals(game.Settings.GameDirectory, StringComparison.OrdinalIgnoreCase) == true) return;
        var root = Path.Combine(game.Settings.GameDirectory, "Mods");
        var saved = new Snapshot { GameDirectory = game.Settings.GameDirectory,
            Mods = mods.Select(m => new Record { Identity = Identity(root, m.DirectoryPath), UniqueId = m.UniqueId, Enabled = m.Enabled }).ToList() };
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonConvert.SerializeObject(saved), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private Snapshot? Load()
    {
        NoLinks(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Mod 恢复记录过大。");
        try
        {
            var saved = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(path));
            if (saved?.GameDirectory == null || saved.Mods == null || saved.Mods.Count > 5000 || saved.Mods.Any(m => m.Identity == null || m.UniqueId == null))
                throw new InvalidDataException("Mod 恢复记录损坏。");
            return saved;
        }
        catch (JsonException ex) { throw new InvalidDataException("Mod 恢复记录损坏，请检查 mod-restore.json。", ex); }
    }
    private static string Identity(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return Path.Combine(Path.GetDirectoryName(relative) ?? "", Path.GetFileName(relative).TrimStart('.'));
    }
    private static void NoLinks(string path)
    {
        for (var part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Mod 恢复路径不支持目录链接。");
    }
    private sealed class Snapshot { public string GameDirectory { get; set; } = ""; public List<Record> Mods { get; set; } = new(); }
    private sealed class Record { public string Identity { get; set; } = ""; public string UniqueId { get; set; } = ""; public bool Enabled { get; set; } }
}
