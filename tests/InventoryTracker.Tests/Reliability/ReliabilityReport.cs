using System.Globalization;
using System.Text;
using System.Text.Json;

namespace InventoryTracker.Tests.Reliability;

/// <summary>What one study run measured, and how it compares with the baseline.</summary>
public sealed class ReliabilityReport(string corpus, int files, long bytes)
{
    public string Corpus { get; } = corpus;
    public int Files { get; } = files;
    public long Bytes { get; } = bytes;

    public Dictionary<string, double> Metrics { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> MissedByType { get; } = new(StringComparer.Ordinal);
    public List<string> MissedExamples { get; } = [];
    public Dictionary<string, int> Caveats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> DriftTags { get; } = new(StringComparer.Ordinal);
    public List<string> LiveDiffs { get; } = [];

    /// <summary>
    /// How far a metric may slip before the ratchet calls it a regression. Covers rounding
    /// in the recorded baseline; counts are whole numbers, so for them it means "any".
    /// </summary>
    private const double Tolerance = 0.002;

    /// <summary>Hard invariants broken by this run, regardless of any baseline.</summary>
    public IReadOnlyList<string> LimitViolations() =>
        ReliabilityMetrics.All
            .Where(m => m.Limit is { } limit && Metrics.TryGetValue(m.Key, out var v)
                        && (m.HigherIsBetter ? v < limit : v > limit))
            .Select(m => $"{m.Key} = {Format(m, Metrics[m.Key])}, must be {(m.HigherIsBetter ? ">=" : "<=")} {Format(m, m.Limit!.Value)}")
            .ToList();

    /// <summary>Metrics that moved the wrong way since <paramref name="baseline"/>.</summary>
    public IReadOnlyList<string> Regressions(BaselineEntry baseline) =>
        ReliabilityMetrics.All
            .Where(m => Metrics.ContainsKey(m.Key) && baseline.Metrics.ContainsKey(m.Key))
            .Where(m => Delta(m, baseline) < -Tolerance)
            .Select(m => $"{m.Key}: {Format(m, baseline.Metrics[m.Key])} -> {Format(m, Metrics[m.Key])}")
            .ToList();

    /// <summary>Whether <paramref name="baseline"/> was recorded over this same set of files.</summary>
    public bool SameCorpusAs(BaselineEntry baseline) => baseline.Files == Files && baseline.Bytes == Bytes;

    /// <summary>Improvement since the baseline, signed so that positive is always better.</summary>
    private double Delta(MetricDef m, BaselineEntry baseline)
    {
        var d = Metrics[m.Key] - baseline.Metrics[m.Key];
        return m.HigherIsBetter ? d : -d;
    }

    public BaselineEntry ToBaseline() => new()
    {
        Files = Files,
        Bytes = Bytes,
        Metrics = ReliabilityMetrics.All
            .Where(m => Metrics.ContainsKey(m.Key))
            .ToDictionary(m => m.Key, m => Math.Round(Metrics[m.Key], 4)),
    };

    public string ToMarkdown(BaselineEntry? baseline)
    {
        var comparable = baseline is not null && SameCorpusAs(baseline);
        var sb = new StringBuilder();

        sb.AppendLine($"# Replay reliability — `{Corpus}`");
        sb.AppendLine();
        sb.AppendLine($"{Files} log file(s), {Bytes / 1024.0 / 1024.0:F1} MB, {Get("lines"):N0} lines, {Get("moves"):N0} moves, {Get("holdings.rows"):N0} holdings.");
        sb.AppendLine();
        if (baseline is null) sb.AppendLine("_No baseline recorded for this corpus yet._");
        else if (!comparable) sb.AppendLine("_The corpus changed since the baseline was recorded; the comparison is indicative only._");
        sb.AppendLine();

        sb.AppendLine("| Metric | Value | Baseline | Target | Status |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var m in ReliabilityMetrics.All)
        {
            if (!Metrics.TryGetValue(m.Key, out var v)) continue;

            var hasBase = baseline?.Metrics.TryGetValue(m.Key, out var b) == true;
            var baseCell = hasBase ? Format(m, baseline!.Metrics[m.Key]) : "—";
            sb.AppendLine($"| {m.Label} `{m.Key}` | {Format(m, v)} | {baseCell} | {Format(m, m.Target)} | {Status(m, v, hasBase ? baseline : null)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Ledger");
        sb.AppendLine();
        foreach (var (key, value) in Counts.Where(kv => kv.Key.StartsWith("ledger.", StringComparison.Ordinal)))
        {
            sb.AppendLine($"- `{key}`: {value:N0}");
        }

        Section(sb, "Relocating requests never captured, by type", MissedByType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}: {kv.Value}"));
        Section(sb, "Examples of missed requests", MissedExamples.Select(l => $"`{l}`"));
        Section(sb, "Tags that drifted", DriftTags.Select(kv => $"`<{kv.Key}>`: {kv.Value}"));
        Section(sb, "Caveats on holdings", Caveats.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} × {kv.Key}"));
        Section(sb, "Live tailing differences", LiveDiffs.Select(d => $"`{d}`"));

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        foreach (var item in list) sb.AppendLine($"- {item}");
    }

    private string Status(MetricDef m, double v, BaselineEntry? baseline)
    {
        if (m.Limit is { } limit && (m.HigherIsBetter ? v < limit : v > limit)) return "✖ breaks invariant";
        if (baseline is not null && Delta(m, baseline) < -Tolerance) return "▼ regressed";

        var met = m.HigherIsBetter ? v >= m.Target : v <= m.Target;
        if (met) return "✔ target met";

        var gap = Math.Abs(m.Target - v);
        var gapText = m.IsRate ? $"{gap * 100:F1} pts" : gap.ToString("0.###", CultureInfo.InvariantCulture);
        return baseline is not null && Delta(m, baseline) > Tolerance
            ? $"▲ improved, {gapText} to go"
            : $"{gapText} to go";
    }

    private long Get(string key) => Counts.GetValueOrDefault(key);

    private static string Format(MetricDef m, double v) =>
        m.IsRate ? $"{v * 100:F1} %" : v.ToString("0.###", CultureInfo.InvariantCulture);

    public string ToJson() => JsonSerializer.Serialize(
        new { Corpus, Files, Bytes, Metrics, Counts, MissedByType, DriftTags, Caveats },
        new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>One corpus's recorded metrics — the ratchet the study is held to.</summary>
public sealed class BaselineEntry
{
    public int Files { get; set; }
    public long Bytes { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Committed at <c>Reliability/baseline.json</c>. A run that improves a metric does not move
/// the baseline by itself; re-record it deliberately with
/// <c>INVENTORYTRACKER_UPDATE_BASELINE=1</c> so the improvement becomes the new floor.
/// </summary>
public sealed class Baseline
{
    public Dictionary<string, BaselineEntry> Corpora { get; set; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Baseline Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path)) ?? new() : new();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");
}
