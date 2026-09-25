using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace InventoryTracker.Tests.Reliability;

/// <summary>
/// The replay reliability study, run as tests. See docs/reliability/README.md.
/// <para>
/// Each run writes a Markdown and JSON report to <c>artifacts/reliability/</c> and holds the
/// corpus to two things: its hard invariants (no unreadable move lines, live tailing equal
/// to a single pass), and its baseline — no metric may get worse than the last recorded run.
/// Improving a metric and re-recording the baseline is how the objective is ratcheted up.
/// </para>
/// </summary>
public sealed class ReliabilityStudyTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Points the study at a real log directory (or a single .log file).</summary>
    public const string CorpusVariable = "INVENTORYTRACKER_CORPUS";

    /// <summary>Set to 1 to record the current run as the corpus's new baseline.</summary>
    public const string UpdateVariable = "INVENTORYTRACKER_UPDATE_BASELINE";

    private readonly string _work = Path.Combine(Path.GetTempPath(), $"itrk-study-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { /* a pooled handle outlived the clear; the temp dir is harmless */ }
    }

    /// <summary>
    /// Inventory lines from three real sessions, player name replaced, covering both logging
    /// formats: the original 4.9 one and the build 12519617 rename. Committed, so the study
    /// runs on every build.
    /// </summary>
    [Fact]
    public void Sample_corpus_keeps_its_invariants_and_does_not_regress()
    {
        Study("sample", Path.Combine(SourceDir(), "Corpus", "sample"), requireBaseline: true);
    }

    /// <summary>
    /// The same study over a corpus of your own. Does nothing unless
    /// <see cref="CorpusVariable"/> is set, since real logs are never committed.
    /// </summary>
    [Fact]
    public void Local_corpus_study()
    {
        var path = Environment.GetEnvironmentVariable(CorpusVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine($"{CorpusVariable} is not set; skipping the local corpus study.");
            return;
        }

        var logDir = path;
        if (File.Exists(path))
        {
            // A lone file is studied as the live Game.log of a directory of its own.
            logDir = Path.Combine(_work, "corpus");
            Directory.CreateDirectory(logDir);
            File.Copy(path, Path.Combine(logDir, "Game.log"));
        }

        var name = Environment.GetEnvironmentVariable("INVENTORYTRACKER_CORPUS_NAME")
                   ?? "local-" + Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        Study(name, logDir, requireBaseline: false);
    }

    private void Study(string corpus, string logDir, bool requireBaseline)
    {
        var report = ReliabilityStudy.Run(corpus, logDir, Path.Combine(_work, corpus));

        var baselinePath = Path.Combine(SourceDir(), "baseline.json");
        var baseline = Baseline.Load(baselinePath);
        var entry = baseline.Corpora.GetValueOrDefault(corpus);

        var markdown = report.ToMarkdown(entry);
        var reports = Path.Combine(RepoRoot(), "artifacts", "reliability");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, $"{corpus}.md"), markdown);
        File.WriteAllText(Path.Combine(reports, $"{corpus}.json"), report.ToJson());
        output.WriteLine(markdown);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            baseline.Corpora[corpus] = report.ToBaseline();
            baseline.Save(baselinePath);
            output.WriteLine($"Baseline for '{corpus}' recorded in {baselinePath}.");
            entry = baseline.Corpora[corpus];
        }

        Assert.Empty(report.LimitViolations());

        if (entry is null)
        {
            Assert.False(requireBaseline, $"No baseline for '{corpus}'. Record one with {UpdateVariable}=1.");
            return;
        }

        if (!report.SameCorpusAs(entry))
        {
            // Different files measure different things; holding one to the other's numbers
            // would fail on the corpus, not the code.
            Assert.False(requireBaseline, $"The '{corpus}' corpus changed since its baseline. Re-record it with {UpdateVariable}=1.");
            output.WriteLine($"The '{corpus}' corpus changed since its baseline; not comparing.");
            return;
        }

        Assert.Empty(report.Regressions(entry));
    }

    private static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>tests/InventoryTracker.Tests/Reliability → repository root.</summary>
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(SourceDir(), "..", "..", ".."));
}
