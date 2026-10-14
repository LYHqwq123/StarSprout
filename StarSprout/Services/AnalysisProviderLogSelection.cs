using System.Text;
using System.Text.RegularExpressions;

namespace StarSprout.Services;

internal static class AnalysisLogSelection
{
    private static readonly (string Name, int Score, Regex Pattern)[] Rules =
    {
        ("ERROR", 100, Rule(@"\bERROR\b|\bfatal\b")),
        ("exception", 95, Rule(@"\b(?:[A-Za-z]*Exception|stack trace)\b")),
        ("missing/requires dependency", 90, Rule(@"\b(?:missing|requires?|required dependency|not installed|dependency|dependencies)\b|缺少|依赖")),
        ("couldn't load", 90, Rule(@"could(?:n.t| not) load|failed to load|skipped mods|无法加载|未能加载")),
        ("incompatible/outdated", 85, Rule(@"\b(?:incompatible|outdated|not compatible|unsupported|obsolete)\b|不兼容|过时")),
        ("duplicate UniqueID", 85, Rule(@"\bduplicate\b|same unique.?id|重复")),
        ("patch conflict", 80, Rule(@"patch.{0,35}(?:conflict|failed|error|invalid)|conflict.{0,30}patch|补丁冲突")),
        ("WARN", 60, Rule(@"\bWARN(?:ING)?\b")),
        ("Content Patcher", 20, Rule(@"Content[ ._-]?Patcher|Pathoschild\.ContentPatcher"))
    };
    private static Regex Rule(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Timestamp = Rule(@"^\[?\d{1,2}:\d{2}:\d{2}(?:\.\d+)?\s*");
    private static readonly Regex Version = Rule(@"SMAPI\s+\d|Stardew(?: Valley)?\s+\d|(?:game|SMAPI) version\b");
    internal sealed class Selection
    {
        public string Text { get; init; } = "";
        public List<string> MatchedKeywords { get; init; } = new();
        public List<string> SignalLines { get; init; } = new();
    }

    internal static Selection Select(string log, bool compact, int budget, string keywords)
    {
        if (log.Length == 0) return new Selection();
        var lines = log.Replace("\r\n", "\n").Split('\n');
        var extras = keywords.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).Where(k => k.Length > 0).ToArray();
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var signals = new List<(int Index, int Score)>();
        var versions = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            // Per-line limits bound regex work without losing the start of a long error message.
            var line = lines[i].Length > 4000 ? lines[i][..4000] : lines[i];
            var score = 0;
            foreach (var (name, ruleScore, pattern) in Rules)
            {
                if (!pattern.IsMatch(line)) continue;
                score = Math.Max(score, ruleScore); matches.Add(name);
            }
            foreach (var keyword in extras)
            {
                if (line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matches.Add(keyword); score = Math.Max(score, 40);
            }
            if (score > 0) signals.Add((i, score));
            if (versions.Count < 4 && Version.IsMatch(line)) versions.Add(i);
        }

        // A timestamp-changing repeated error is one piece of evidence, with a repeat count.
        var unique = new Dictionary<string, (int Index, int Score, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, score) in signals)
        {
            var canonical = Timestamp.Replace(lines[index], "").Trim();
            if (unique.TryGetValue(canonical, out var existing)) unique[canonical] = (existing.Index, Math.Max(existing.Score, score), existing.Count + 1);
            else unique[canonical] = (index, score, 1);
        }
        var ranked = unique.Values.OrderByDescending(x => x.Score).ThenBy(x => x.Index).ToList();
        var selected = new SortedDictionary<int, string>();
        var chars = 0;
        void Add(int index, bool signal = false, int count = 1)
        {
            if (index < 0 || index >= lines.Length || selected.ContainsKey(index) || string.IsNullOrWhiteSpace(lines[index])) return;
            var canonical = Timestamp.Replace(lines[index], "").Trim();
            // Do not re-introduce later duplicates through another error's context.
            if (!signal && unique.TryGetValue(canonical, out var first) && first.Index != index) return;
            var text = $"L{index + 1}: " + lines[index];
            var maxLine = signal ? Math.Min(2000, Math.Max(400, budget / 3)) : 450;
            if (text.Length > maxLine) text = text[..maxLine] + " [长行省略]";
            if (count > 1) text += $" [重复 {count} 次]";
            if (chars + text.Length + 1 > budget) return;
            selected[index] = text; chars += text.Length + 1;
        }
        foreach (var index in versions) Add(index);
        if (!compact && log.Length <= budget)
        {
            // The explicit non-compact option keeps a short full log, still bounded overall.
            return new Selection { Text = log, MatchedKeywords = matches.Take(64).ToList(), SignalLines = ranked.Take(12).Select(s => lines[s.Index]).ToList() };
        }
        // Allocate evidence before context, preserving early severe errors even in large logs.
        foreach (var item in ranked.Take(100)) Add(item.Index, true, item.Count);
        foreach (var item in ranked.Take(40))
        {
            Add(item.Index - 1);
            Add(item.Index + 1);
            if (item.Score >= 90) { Add(item.Index + 2); Add(item.Index + 3); }
        }
        if (selected.Count == 0)
        {
            foreach (var index in Enumerable.Range(0, Math.Min(4, lines.Length))) Add(index);
            foreach (var index in Enumerable.Range(Math.Max(4, lines.Length - 4), Math.Max(0, Math.Min(4, lines.Length - 4)))) Add(index);
        }
        var textResult = string.Join("\n", selected.Values);
        return new Selection
        {
            Text = textResult, MatchedKeywords = matches.Take(64).ToList(),
            SignalLines = ranked.Take(12).Select(s => $"L{s.Index + 1}: " + lines[s.Index]).ToList()
        };
    }

    internal static List<StardewMod> RelatedMods(List<StardewMod> mods, string evidence)
    {
        var result = new HashSet<StardewMod>();
        foreach (var mod in mods)
        {
            if ((mod.Status != "可用" && mod.Status != "已停用") ||
                (mod.UniqueId.Length > 0 && evidence.Contains(mod.UniqueId, StringComparison.OrdinalIgnoreCase)) ||
                (mod.Name.Length > 3 && evidence.Contains(mod.Name, StringComparison.OrdinalIgnoreCase))) result.Add(mod);
        }
        // Include actual dependencies of relevant packs, with their enabled/status metadata.
        var queue = new Queue<StardewMod>(result);
        while (queue.TryDequeue(out var mod))
            foreach (var dependency in mod.Dependencies)
                foreach (var candidate in mods.Where(m => m.UniqueId.Equals(dependency.UniqueId, StringComparison.OrdinalIgnoreCase)))
                    if (result.Add(candidate)) queue.Enqueue(candidate);
        return result.OrderByDescending(m => m.Status != "可用" && m.Status != "已停用").ThenBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
