using Xunit;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests.Reliability;

public sealed class ReliabilityReportTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"itrk-report-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    [Fact]
    public void Corpus_identity_includes_file_path_and_content_fingerprint()
    {
        var corpus = Path.Combine(_work, "corpus");
        Directory.CreateDirectory(corpus);
        var file = Path.Combine(corpus, "Game.log");

        File.WriteAllText(file, "AAAA");
        var first = ReliabilityStudy.Run("same-size", corpus, Path.Combine(_work, "run-1"));
        var baseline = first.ToBaseline();
        var repeated = ReliabilityStudy.Run("same-size", corpus, Path.Combine(_work, "run-same"));
        Assert.True(repeated.SameCorpusAs(baseline));

        File.WriteAllText(file, "BBBB");
        var second = ReliabilityStudy.Run("same-size", corpus, Path.Combine(_work, "run-2"));

        Assert.Equal(first.Files, second.Files);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.False(second.SameCorpusAs(baseline));

        File.Delete(file);
        var backupDir = Path.Combine(corpus, "logbackups");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "Game Build(12344265).log"), "BBBB");
        var renamed = ReliabilityStudy.Run("same-size", corpus, Path.Combine(_work, "run-3"));

        Assert.Equal(second.Files, renamed.Files);
        Assert.Equal(second.Bytes, renamed.Bytes);
        Assert.False(renamed.SameCorpusAs(second.ToBaseline()));
    }

    [Fact]
    public void Legacy_baseline_without_manifest_still_reports_metric_regressions()
    {
        var report = new ReliabilityReport("legacy", files: 1, bytes: 4);
        report.Metrics["holdings.mean_score"] = 0.7;
        var baseline = new BaselineEntry
        {
            Files = 1,
            Bytes = 4,
            Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["holdings.mean_score"] = 0.8,
            },
        };

        Assert.False(report.SameCorpusAs(baseline));
        Assert.True(report.SameSizeAs(baseline));
        Assert.Contains(report.Regressions(baseline), regression => regression.Contains("holdings.mean_score", StringComparison.Ordinal));
        Assert.Contains("identity cannot be verified", report.ToMarkdown(baseline), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Placement_calibration_is_temporal_reproducible_and_independent_of_evaluation_labels()
    {
        var study = typeof(ReliabilityStudy);
        var observationType = study.GetNestedType("PlacementObservation", BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(observationType is not null, "ReliabilityStudy must expose the placement observation seam for split regression tests.");

        var calibrate = study.GetMethod("CalibratePlacements", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(calibrate is not null, "ReliabilityStudy.CalibratePlacements is required to prove evaluation labels do not train factors.");

        var createObservation = observationType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(DateTimeOffset), typeof(int), typeof(bool)],
            modifiers: null);
        Assert.True(createObservation is not null, "PlacementObservation must accept a timestamp, build, and observed placement label.");

        var observations = new List<object>();
        for (var day = 1; day <= 8; day++)
        {
            observations.Add(createObservation!.Invoke([new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), 42, day % 2 == 0]));
            observations.Add(createObservation!.Invoke([new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), 43, day % 2 == 0]));
        }
        observations.Add(createObservation!.Invoke([new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero), 44, true]));
        observations.Add(createObservation!.Invoke([new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero), 0, false]));

        var input = MakeTypedList(observationType!, observations);
        var first = calibrate!.Invoke(null, [input])!;
        var repeated = calibrate.Invoke(null, [input])!;
        var reordered = calibrate.Invoke(null, [MakeTypedList(observationType!, observations.AsEnumerable().Reverse())])!;
        var calibration = ReadList(first, "Calibration");
        var evaluation = ReadList(first, "Evaluation");
        Assert.NotEmpty(calibration);
        Assert.NotEmpty(evaluation);

        var calibrationTimes = calibration.Select(ReadTimestamp).ToArray();
        var evaluationTimes = evaluation.Select(ReadTimestamp).ToArray();
        Assert.Empty(calibrationTimes.Intersect(evaluationTimes));
        Assert.True(calibrationTimes.Max() < evaluationTimes.Min(), "Evaluation observations must be later than calibration observations.");
        Assert.Equal(JsonSerializer.Serialize(calibrationTimes), JsonSerializer.Serialize(ReadList(repeated, "Calibration").Select(ReadTimestamp).ToArray()));
        Assert.Equal(JsonSerializer.Serialize(evaluationTimes), JsonSerializer.Serialize(ReadList(repeated, "Evaluation").Select(ReadTimestamp).ToArray()));
        Assert.Equal(JsonSerializer.Serialize(calibrationTimes), JsonSerializer.Serialize(ReadList(reordered, "Calibration").Select(ReadTimestamp).ToArray()));
        Assert.Equal(JsonSerializer.Serialize(evaluationTimes), JsonSerializer.Serialize(ReadList(reordered, "Evaluation").Select(ReadTimestamp).ToArray()));
        Assert.Equal(JsonSerializer.Serialize(ReadProperty(first, "LearnedFactors")), JsonSerializer.Serialize(ReadProperty(repeated, "LearnedFactors")));
        Assert.Equal(JsonSerializer.Serialize(ReadProperty(first, "LearnedFactors")), JsonSerializer.Serialize(ReadProperty(reordered, "LearnedFactors")));
        Assert.Contains(44, ((IEnumerable)ReadProperty(first, "WeakBuilds")!).Cast<int>());
        Assert.Contains(0, ((IEnumerable)ReadProperty(first, "WeakBuilds")!).Cast<int>());
        Assert.Equal(0.25, (double)ReadProperty(first, "EvaluationBrier")!, precision: 10);

        var changedLabels = observations.Select(observation =>
        {
            var at = (DateTimeOffset)observationType!.GetProperty("At")!.GetValue(observation)!;
            var build = (int)observationType.GetProperty("Build")!.GetValue(observation)!;
            var label = (bool)observationType.GetProperty("SamePlace")!.GetValue(observation)!;
            return createObservation!.Invoke([at, build, evaluationTimes.Contains(at) ? !label : label]);
        }).ToList();
        var changed = calibrate.Invoke(null, [MakeTypedList(observationType!, changedLabels)])!;

        Assert.Equal(JsonSerializer.Serialize(ReadProperty(first, "LearnedFactors")), JsonSerializer.Serialize(ReadProperty(changed, "LearnedFactors")));
        Assert.NotEqual(
            JsonSerializer.Serialize(evaluation.Select(ReadLabel).ToArray()),
            JsonSerializer.Serialize(ReadList(changed, "Evaluation").Select(ReadLabel).ToArray()));
        Assert.True(ReadProperty(first, "EvaluationBrier") is not null, "The held-out score must be reported separately from any in-sample score.");
    }

    [Fact]
    public void Study_outputs_separate_example_counts_accuracy_unit_accounting_and_confidence()
    {
        var report = ReliabilityStudy.Run("sample", Path.Combine(SourceDir(), "Corpus", "sample"), Path.Combine(_work, "study"));
        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;
        var counts = root.GetProperty("Counts");
        var metrics = root.GetProperty("Metrics");

        Assert.True(counts.TryGetProperty("placement.examples", out _), "Report placement example count explicitly.");
        Assert.True(metrics.TryGetProperty("placement.accuracy", out _), "Report same-place / total placement accuracy separately from scores.");
        Assert.True(counts.TryGetProperty("ledger.class_units", out _), "Report the denominator for unit accounting.");
        Assert.True(counts.TryGetProperty("ledger.units_accounted_proxy", out _), "Report an explicitly bounded unit-accounting proxy, not ground-truth correctness.");
        Assert.True(metrics.TryGetProperty("placement.in_sample_brier", out _), "Keep the production/in-sample score explicit.");
        Assert.True(counts.TryGetProperty("placement.evaluation.incidences", out var evaluationIncidences), "Report the holdout denominator in check-update incidences.");
        if (evaluationIncidences.GetInt64() > 0)
            Assert.True(metrics.TryGetProperty("placement.evaluation_brier", out _), "Keep held-out evaluation score distinct.");
        else
            Assert.False(metrics.TryGetProperty("placement.evaluation_brier", out _), "Do not fabricate a score without held-out support.");
        Assert.True(metrics.TryGetProperty("holdings.mean_score", out _), "Keep resolver confidence score distinct from placement accuracy.");
        Assert.True(metrics.TryGetProperty("holdings.high_share", out _), "Report the confidence band separately from accuracy.");

        var markdown = report.ToMarkdown(null);
        Assert.Contains("no independent inventory ground truth", markdown, StringComparison.OrdinalIgnoreCase);
    }

    private static object MakeTypedList(Type elementType, IEnumerable<object?> items)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var item in items) list.Add(item);
        return list;
    }

    private static List<object> ReadList(object value, string property) =>
        ((IEnumerable)ReadProperty(value, property)!).Cast<object>().ToList();

    private static DateTimeOffset ReadTimestamp(object observation) =>
        (DateTimeOffset)ReadProperty(observation, "At")!;

    private static bool ReadLabel(object observation) =>
        (bool)ReadProperty(observation, "SamePlace")!;

    private static object? ReadProperty(object value, string property) =>
        value.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value);

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
