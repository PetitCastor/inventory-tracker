using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InventoryTracker.Tests.Reliability;

/// <summary>What one study run measured, and how it compares with the baseline.</summary>
public sealed class ReliabilityReport(string corpus, int files, long bytes)
{
    public string Corpus { get; } = corpus;
    public int Files { get; } = files;
    public long Bytes { get; } = bytes;

    /// <summary>Files that define this corpus, ordered by normalized relative path.</summary>
    private readonly List<CorpusFileIdentity> _manifest = [];
    public IReadOnlyList<CorpusFileIdentity> Manifest => _manifest;

    /// <summary>SHA-256 of the canonical file manifest, or null when identity was not measured.</summary>
    public string? CorpusFingerprint { get; private set; }

    public DateTimeOffset? PlacementCalibrationThrough { get; set; }
    public DateTimeOffset? PlacementEvaluationFrom { get; set; }
    public Dictionary<int, int> PlacementCalibrationSupportByBuild { get; } = [];
    public Dictionary<int, double> PlacementLearnedFactors { get; } = [];
    public List<int> PlacementWeakBuilds { get; } = [];

    public Dictionary<string, double> Metrics { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> MissedByType { get; } = new(StringComparer.Ordinal);
    public List<string> MissedExamples { get; } = [];
    public Dictionary<string, int> Caveats { get; } = new(StringComparer.Ordinal);
    public List<string> LiveDiffs { get; } = [];
    public Dictionary<string, int> MissCauses { get; } = new(StringComparer.Ordinal);
    public List<string> MissExamples { get; } = [];
    public List<string> PlacementTable { get; } = [];

    /// <summary>
    /// How far a metric may slip before the ratchet calls it a regression. Covers rounding
    /// in the recorded baseline; counts are whole numbers, so for them it means "any".
    /// </summary>
    private const double Tolerance = 0.002;

    public void SetManifest(IEnumerable<CorpusFileIdentity> files)
    {
        _manifest.Clear();
        _manifest.AddRange(files.OrderBy(f => f.Path, StringComparer.Ordinal));
        var canonical = JsonSerializer.Serialize(_manifest);
        CorpusFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Hard invariants broken by this run, regardless of any baseline.</summary>
    public IReadOnlyList<string> LimitViolations() =>
        ReliabilityMetrics.All
            .Where(m => m.Limit is { } limit && Metrics.TryGetValue(m.Key, out var v) && Breaks(m, v, limit))
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
    public bool SameCorpusAs(BaselineEntry baseline) =>
        !string.IsNullOrWhiteSpace(CorpusFingerprint)
        && !string.IsNullOrWhiteSpace(baseline.CorpusFingerprint)
        && string.Equals(CorpusFingerprint, baseline.CorpusFingerprint, StringComparison.Ordinal);

    public bool SameSizeAs(BaselineEntry baseline) => baseline.Files == Files && baseline.Bytes == Bytes;

    private static bool Breaks(MetricDef m, double value, double limit) =>
        m.HigherIsBetter ? value < limit : value > limit;

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
        Manifest = Manifest.ToList(),
        CorpusFingerprint = CorpusFingerprint,
        Metrics = ReliabilityMetrics.All
            .Where(m => Metrics.ContainsKey(m.Key))
            .ToDictionary(m => m.Key, m => Math.Round(Metrics[m.Key], 4)),
    };

    public string ToMarkdown(BaselineEntry? baseline)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Replay reliability — `{Corpus}`");
        sb.AppendLine();
        sb.AppendLine(
            $"{Files} log file(s), {Bytes / 1024.0 / 1024.0:F1} MB, {Get("lines"):N0} lines, " +
            $"{Get("moves"):N0} moves, {Get("holdings.rows"):N0} holdings.");
        sb.AppendLine();
        if (baseline is null) sb.AppendLine("_No baseline recorded for this corpus yet._");
        else if (!SameCorpusAs(baseline) && string.IsNullOrWhiteSpace(baseline.CorpusFingerprint) && SameSizeAs(baseline))
            sb.AppendLine("_Legacy baseline: corpus identity cannot be verified; metric comparisons still apply._");
        else if (!SameCorpusAs(baseline)) sb.AppendLine("_The corpus differs from its baseline; the comparison is indicative only._");
        sb.AppendLine();

        sb.AppendLine("| Metric | Value | Baseline | Target | Status |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var m in ReliabilityMetrics.All)
        {
            if (!Metrics.TryGetValue(m.Key, out var v)) continue;

            var hasBase = baseline?.Metrics.ContainsKey(m.Key) == true;
            var baseCell = hasBase ? Format(m, baseline!.Metrics[m.Key]) : "—";
            sb.AppendLine($"| {m.Label} `{m.Key}` | {Format(m, v)} | {baseCell} | {Format(m, m.Target)} | {Status(m, v, hasBase ? baseline : null)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Evidence limits");
        sb.AppendLine();
        sb.AppendLine("There is no independent inventory ground truth. Placement accuracy is agreement with later named move sources; unit accounting is a replay proxy bounded by class-level units observed in the logs. Confidence scores are resolver heuristics, not validated probabilities.");

        sb.AppendLine();
        sb.AppendLine("## Temporal placement evaluation");
        sb.AppendLine();
        if (PlacementCalibrationThrough is { } calibrationThrough && PlacementEvaluationFrom is { } evaluationFrom)
        {
            sb.AppendLine($"Calibration uses check-update incidences through {calibrationThrough:O}; evaluation starts at {evaluationFrom:O}. Same-timestamp incidences stay together.");
            sb.AppendLine($"Learned factors: {PlacementLearnedFactors.Count}; weak or unsupported evaluation builds: {(PlacementWeakBuilds.Count == 0 ? "none" : string.Join(", ", PlacementWeakBuilds))}.");
        }
        else
        {
            sb.AppendLine("No temporal holdout is available; there are not enough distinct timestamps with update evidence.");
        }

        sb.AppendLine();
        sb.AppendLine("## Ledger");
        sb.AppendLine();
        foreach (var (key, value) in Counts.Where(kv => kv.Key.StartsWith("ledger.", StringComparison.Ordinal)))
        {
            sb.AppendLine($"- `{key}`: {value:N0}");
        }

        if (Counts.TryGetValue("placement.examples", out var placementExamples))
        {
            sb.AppendLine();
            sb.AppendLine("## Placement summary");
            sb.AppendLine();
            var same = Counts.GetValueOrDefault("placement.same_place");
            sb.AppendLine($"- Same place: {same:N0}/{placementExamples:N0} examples");
            if (Metrics.TryGetValue("placement.accuracy", out var accuracy))
                sb.AppendLine($"- Placement agreement: {(accuracy * 100).ToString("0.0", CultureInfo.InvariantCulture)} %");
            sb.AppendLine($"- Calibration incidences: {Counts.GetValueOrDefault("placement.calibration.incidences"):N0}");
            sb.AppendLine($"- Evaluation incidences: {Counts.GetValueOrDefault("placement.evaluation.incidences"):N0}");
            if (Metrics.TryGetValue("placement.in_sample_brier", out var inSampleBrier))
                sb.AppendLine($"- In-sample Brier score: {inSampleBrier.ToString("0.###", CultureInfo.InvariantCulture)}");
            if (Metrics.TryGetValue("placement.evaluation_brier", out var evaluationBrier))
                sb.AppendLine($"- Holdout Brier score: {evaluationBrier.ToString("0.###", CultureInfo.InvariantCulture)}");
            else
                sb.AppendLine("- Holdout Brier score: unavailable (no evaluation support)");
            if (PlacementCalibrationSupportByBuild.Count > 0)
            {
                sb.AppendLine("- Calibration support by update build:");
                foreach (var (build, support) in PlacementCalibrationSupportByBuild.OrderBy(kv => kv.Key))
                    sb.AppendLine($"  - build {build}: {support} incidence(s)");
            }
        }

        if (PlacementTable.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Placement checks by age");
            sb.AppendLine();
            sb.AppendLine("Named moves out of a real source, compared with where the ledger believed the entity was.");
            sb.AppendLine("This is what the resolver's age and update penalties are calibrated against.");
            sb.AppendLine();
            foreach (var row in PlacementTable) sb.AppendLine(row);
        }

        Section(sb, "Class-level units not found at their source, by cause", MissCauses.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} × {kv.Key}"));
        Section(sb, "Examples of source misses", MissExamples.Select(l => $"`{l}`"));
        Section(sb, "Relocating requests never captured, by type", MissedByType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}: {kv.Value}"));
        Section(sb, "Examples of missed requests", MissedExamples.Select(l => $"`{l}`"));
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
        if (m.Limit is { } limit && Breaks(m, v, limit)) return "✖ breaks invariant";
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
        new
        {
            Corpus, Files, Bytes, CorpusFingerprint, Manifest,
            PlacementCalibrationThrough, PlacementEvaluationFrom, PlacementCalibrationSupportByBuild,
            PlacementLearnedFactors, PlacementWeakBuilds,
            EvidenceLimits = "No independent inventory ground truth; placement is agreement with later named sources, unit accounting is a replay proxy, and confidence scores are heuristics.",
            Metrics, Counts, MissedByType, Caveats, MissCauses,
        },
        new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>One discovered corpus file and its content identity.</summary>
public sealed record CorpusFileIdentity(string Path, long Bytes, string Sha256);

/// <summary>One corpus's recorded metrics — the ratchet the study is held to.</summary>
public sealed class BaselineEntry
{
    public int Files { get; set; }
    public long Bytes { get; set; }
    public List<CorpusFileIdentity>? Manifest { get; set; }
    public string? CorpusFingerprint { get; set; }
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
