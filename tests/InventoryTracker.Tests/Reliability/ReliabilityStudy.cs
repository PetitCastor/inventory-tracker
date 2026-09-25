using System.Globalization;
using System.Text.RegularExpressions;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Resolve;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests.Reliability;

/// <summary>
/// Replays a corpus of Game.log files through the real pipeline — parser, ingestor, ledger,
/// resolver — and measures how much of what the game logged survives each stage, and how
/// much the result can be trusted.
/// <para>
/// Nothing here is a second implementation of the tracker. The capture rate is the one
/// independent measurement: it recognises move requests by their shape alone, so it keeps
/// counting them when the parser stops understanding them. Every other number is read off
/// the pipeline's own output.
/// </para>
/// </summary>
public static class ReliabilityStudy
{
    /// <param name="LiveChunkSeconds">How much log time each simulated watcher pass takes in.</param>
    /// <param name="LiveFiles">How many of the busiest files to replay as a live, growing log.</param>
    public sealed record Options(int LiveChunkSeconds = 5, int LiveFiles = 3);

    /// <summary>
    /// Move types whose source and target are the same grid by design. The ingestor drops
    /// them on purpose, so they are not counted as missed.
    /// </summary>
    private static readonly HashSet<string> SameGridTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Split", "Stack", "AmmoRepool" };

    /// <summary>How far a move row's timestamp may sit from the request line it came from.</summary>
    private static readonly TimeSpan CaptureWindow = TimeSpan.FromSeconds(10);

    public static ReliabilityReport Run(string corpus, string logDir, string workDir, Options? options = null)
    {
        options ??= new Options();
        Directory.CreateDirectory(workDir);

        var files = LogFileLocator.Discover(logDir, InventoryEventParser.Cutoff);
        var report = new ReliabilityReport(
            corpus, files.Count, files.Sum(f => new FileInfo(f.Path).Length));

        var db = new TrackerDb(Path.Combine(workDir, "study.db"));
        db.Initialize();
        var stats = new LogIngestor(db, logDir).IngestAll();

        report.Counts["files"] = stats.FilesSeen;
        report.Counts["lines"] = stats.LinesRead;
        report.Counts["moves"] = stats.MovesInserted;
        report.Counts["geids_recovered"] = stats.GeidsRecovered;
        report.Counts["batch_moves"] = stats.BatchMovesExpanded;
        report.Metrics["capture.unrecognised_lines"] = stats.UnrecognisedMoves;

        MeasureCapture(db, files, report);
        MeasureEnrichment(db, report);

        var resolver = HoldingResolver.Load(db, EndOfCorpus(db));
        MeasureLedger(resolver, report);
        MeasureHoldings(resolver.ResolveAll(), report);

        MeasureLiveParity(db, files, workDir, options, report);

        SqliteConnection.ClearAllPools();
        return report;
    }

    /// <summary>
    /// Counts relocating requests by their Add-move line — the one line every move writes,
    /// in both logging formats seen so far — and checks each landed as at least one move row.
    /// </summary>
    private static void MeasureCapture(TrackerDb db, IReadOnlyList<LogFile> files, ReliabilityReport report)
    {
        var recorded = new Dictionary<(string Path, int Request), List<DateTimeOffset>>();
        using (var cn = db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT s.path, m.request_no, m.ts FROM move m JOIN session s ON s.id = m.session_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = (r.GetString(0), r.GetInt32(1));
                if (!recorded.TryGetValue(key, out var list)) recorded[key] = list = [];
                list.Add(ParseTs(r.GetString(2)));
            }
        }

        var requests = 0;
        var captured = 0;

        foreach (var file in files)
        {
            foreach (var raw in new ByteLineReader().ReadLines(file.Path))
            {
                if (!LogLine.TryParse(raw.Text, out var line) || line.Timestamp < InventoryEventParser.Cutoff) continue;
                if (InventoryEventParser.ReadRequestShape(line) is not { IsAddMove: true } shape) continue;
                if (InventoryEventParser.IsNonRelocating(shape.MoveType) || SameGridTypes.Contains(shape.MoveType)) continue;

                // An Add-move line with no item at all has nothing to move. The game writes
                // these for a handful of UI actions, and they never become a Queued request.
                if (Regex.IsMatch(line.Rest, @"ItemClass\[(|null|none)\] ", RegexOptions.IgnoreCase)) continue;

                requests++;
                var hit = recorded.TryGetValue((file.Path, shape.RequestNo), out var times)
                          && times.Any(t => (t - line.Timestamp).Duration() <= CaptureWindow);

                if (hit)
                {
                    captured++;
                    continue;
                }

                report.MissedByType[shape.MoveType] = report.MissedByType.GetValueOrDefault(shape.MoveType) + 1;
                if (report.MissedExamples.Count < 12) report.MissedExamples.Add(Shorten(line.Raw));
            }
        }

        report.Counts["relocating_requests"] = requests;
        report.Metrics["capture.request_rate"] = requests == 0 ? 1.0 : (double)captured / requests;
    }

    private static void MeasureEnrichment(TrackerDb db, ReliabilityReport report)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(item_geid IS NOT NULL), 0),
                   COALESCE(SUM(lower(result) = 'succeed'), 0)
            FROM move
            """;
        using var r = cmd.ExecuteReader();
        r.Read();

        var moves = r.GetInt64(0);
        report.Metrics["enrich.geid_coverage"] = moves == 0 ? 1.0 : (double)r.GetInt64(1) / moves;
        report.Metrics["enrich.confirmed_rate"] = moves == 0 ? 1.0 : (double)r.GetInt64(2) / moves;
    }

    private static void MeasureLedger(HoldingResolver resolver, ReliabilityReport report)
    {
        var s = resolver.ReplayStats;

        report.Metrics["ledger.accounted_rate"] = s.AccountedRate;
        report.Metrics["ledger.duplicate_risk_rate"] = s.ClassUnits == 0 ? 0 : (double)s.UnitsDuplicateRisk / s.ClassUnits;
        report.Counts["ledger.source_hit_permille"] = (long)Math.Round(s.SourceHitRate * 1000);

        report.Counts["ledger.named_moves"] = s.NamedMoves;
        report.Counts["ledger.class_moves"] = s.ClassMoves;
        report.Counts["ledger.class_units"] = s.ClassUnits;
        report.Counts["ledger.units_from_named"] = s.UnitsFromNamed;
        report.Counts["ledger.units_from_loose"] = s.UnitsFromLoose;
        report.Counts["ledger.units_from_carried"] = s.UnitsFromCarried;
        report.Counts["ledger.units_inferred"] = s.UnitsInferred;
        report.Counts["ledger.units_first_seen"] = s.UnitsFirstSeen;
        report.Counts["ledger.units_duplicate_risk"] = s.UnitsDuplicateRisk;
        report.Counts["ledger.source_unknown"] = s.SourceUnknown;
        report.Counts["ledger.failed_skipped"] = s.FailedSkipped;
        report.Counts["ledger.unreal_target_skipped"] = s.UnrealTargetSkipped;
        report.Counts["ledger.anonymous_reconciled"] = s.AnonymousReconciled;
        report.Counts["ledger.worn_sightings"] = s.WornSightings;
        report.Counts["ledger.carried_used_up"] = s.CarriedUsedUp;
        report.Counts["ledger.relocated_by_update"] = s.RelocatedByUpdate;

        MeasurePlacements(s, resolver.Updates, resolver.UpdateSurvivals, report);

        foreach (var miss in s.Misses)
        {
            var units = miss.Wanted - miss.Found;
            var key = miss.CoveredByCarried
                ? $"{miss.Cause}, found on the player instead"
                : $"{miss.Cause}, {(miss.ElsewhereAt is { } e ? $"class held elsewhere ({e.Kind}): duplicate risk" : "class nowhere else: first seen")}";
            report.MissCauses[key] = report.MissCauses.GetValueOrDefault(key) + units;

            if (report.MissExamples.Count < 40)
            {
                report.MissExamples.Add(
                    $"{miss.At:yyyy-MM-dd HH:mm:ss} {miss.ItemClass} x{units}: {miss.Source.Kind} {miss.Source.Key} -> " +
                    $"{miss.Target.Kind} {miss.Target.Key} [{miss.Cause}" +
                    (miss.CoveredByCarried ? "; found on the player]" : miss.ElsewhereAt is { } w ? $"; held at {w.Kind} {w.Key}]" : "]"));
            }
        }
    }

    private static readonly (string Label, TimeSpan Upto)[] AgeBuckets =
    [
        ("< 1 h", TimeSpan.FromHours(1)),
        ("1 h – 1 d", TimeSpan.FromDays(1)),
        ("1 – 7 d", TimeSpan.FromDays(7)),
        ("7 – 30 d", TimeSpan.FromDays(30)),
        ("> 30 d", TimeSpan.MaxValue),
    ];

    /// <summary>
    /// Agreement between the ledger's belief and the game's named source, by how long the
    /// belief had stood. If placements went stale with age, agreement would fall with it.
    /// </summary>
    private static void MeasurePlacements(
        LedgerReplay.ReplayStats s, IReadOnlyList<HoldingResolver.GameUpdate> updates,
        IReadOnlyDictionary<int, HoldingResolver.UpdateSurvival> survival, ReliabilityReport report)
    {
        var checks = s.PlacementChecks;
        report.Counts["placement.checks"] = checks.Count;
        report.Counts["placement.same_place"] = checks.Count(c => c.SamePlace);
        report.Counts["game_updates"] = updates.Count;
        if (checks.Count == 0) return;

        bool Crossed(LedgerReplay.PlacementCheck c) => updates.Any(u => u.At > c.At - c.Age && u.At <= c.At);

        var within = checks.Where(c => !Crossed(c)).ToList();
        var across = checks.Where(Crossed).ToList();
        report.Counts["placement.within_build"] = within.Count;
        report.Counts["placement.across_update"] = across.Count;

        // The calibration the resolver's scoring rests on: a placement within one build should
        // hold whatever its age, and one across an update should not be trusted as much.
        if (within.Count > 0) report.Metrics["placement.within_build_same_place"] = (double)within.Count(c => c.SamePlace) / within.Count;
        if (across.Count > 0) report.Metrics["placement.across_update_same_place"] = (double)across.Count(c => c.SamePlace) / across.Count;

        // Calibration: the trust the resolver's update weighting puts in each checked placement,
        // against whether the game then confirmed it. Only the update factor is scored — it is
        // the part of the score these checks can test. The factors are learned from these same
        // checks, so this is in-sample until a new corpus arrives.
        double Predicted(LedgerReplay.PlacementCheck c) =>
            updates.Where(u => u.At > c.At - c.Age && u.At <= c.At)
                .Select(u => survival[u.Build].Factor)
                .DefaultIfEmpty(1.0)
                .Min();

        report.Metrics["placement.brier"] = checks.Average(c => Math.Pow(Predicted(c) - (c.SamePlace ? 1 : 0), 2));

        string Bucket(TimeSpan age) => AgeBuckets.First(b => age < b.Upto).Label;
        string Cell(IEnumerable<LedgerReplay.PlacementCheck> cs)
        {
            var list = cs.ToList();
            return list.Count == 0 ? "—" : $"{list.Count(c => c.SamePlace)}/{list.Count}";
        }

        report.PlacementTable.Add("Same place, believed vs. the game's named source:");
        report.PlacementTable.Add("");
        report.PlacementTable.Add("| Age | Within one build | Across a game update |");
        report.PlacementTable.Add("|---|---:|---:|");
        foreach (var (label, _) in AgeBuckets)
        {
            report.PlacementTable.Add(
                $"| {label} | {Cell(within.Where(c => Bucket(c.Age) == label))} | {Cell(across.Where(c => Bucket(c.Age) == label))} |");
        }

        report.PlacementTable.Add("");
        report.PlacementTable.Add("Placements the game contradicted:");
        report.PlacementTable.Add("");
        foreach (var c in checks.Where(c => !c.SamePlace).OrderBy(c => c.At))
        {
            report.PlacementTable.Add(
                $"- `{c.At:yyyy-MM-dd HH:mm} {c.ItemClass} {c.Geid}: believed {c.Believed.Kind} {c.Believed.Key} " +
                $"(by {c.BelievedArrivedBy}, {c.Age.TotalDays:F1} d{(Crossed(c) ? ", across an update" : "")}" +
                $"{(c.BelievedMovedByUpdate is { } b ? $", relocated by {b}" : "")}), " +
                $"moved from {c.Actual.Kind} {c.Actual.Key}`");
        }

        report.PlacementTable.Add("");
        report.PlacementTable.Add("What the resolver learned about each update:");
        report.PlacementTable.Add("");
        foreach (var u in updates)
        {
            var r = survival[u.Build];
            report.PlacementTable.Add(
                $"- build {u.Build}, first played {u.At:yyyy-MM-dd}, spawned at {u.Spawn ?? "unknown"}: " +
                $"{r.Survived}/{r.Checked} placements held, " +
                $"factor ×{r.Factor:0.##}{(r.Verified ? "" : " (unverified default)")}");
        }

        var oldest = checks.Max(c => c.Age);
        report.Counts["placement.oldest_checked_hours"] = (long)oldest.TotalHours;
    }

    private static void MeasureHoldings(IReadOnlyList<ItemHolding> holdings, ReliabilityReport report)
    {
        var n = holdings.Count;
        report.Counts["holdings.rows"] = n;
        report.Counts["holdings.units"] = holdings.Sum(h => h.Quantity);

        double Share(Func<ItemHolding, bool> pick) => n == 0 ? 0 : (double)holdings.Count(pick) / n;

        report.Metrics["holdings.high_share"] = Share(h => h.Confidence == Confidence.High);
        report.Metrics["holdings.low_share"] = Share(h => h.Confidence == Confidence.Low);
        report.Metrics["holdings.mean_score"] = n == 0 ? 1.0 : holdings.Average(h => h.Score);
        report.Metrics["holdings.inferred_share"] = Share(h => h.Caveats.Any(c => c.StartsWith("we saw this arrive", StringComparison.Ordinal)));
        report.Metrics["holdings.first_seen_share"] = Share(h => h.Caveats.Any(c => c.StartsWith("first seen", StringComparison.Ordinal)));

        // Caveats are prose with names and numbers spliced in; fold those out so the same
        // cause is counted as one row of the histogram.
        foreach (var caveat in holdings.SelectMany(h => h.Caveats))
        {
            var shape = Regex.Replace(Regex.Replace(caveat, "'[^']*'", "'…'"), @"\d+", "#");
            report.Caveats[shape] = report.Caveats.GetValueOrDefault(shape) + 1;
        }
    }

    /// <summary>
    /// Replays the busiest files as a growing Game.log, the way the watcher sees them during
    /// play, and compares the outcome with reading each file in one go. Two ways: one
    /// ingestor kept across passes (the app), and a fresh one per pass (a restart before
    /// every pass — only what the store persists carries over).
    /// </summary>
    private static void MeasureLiveParity(
        TrackerDb db, IReadOnlyList<LogFile> files, string workDir, Options options, ReliabilityReport report)
    {
        var busiest = MovesPerFile(db)
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(options.LiveFiles)
            .Select(kv => kv.Key)
            .Where(p => files.Any(f => f.Path == p))
            .ToList();

        long common = 0, union = 0, coldCommon = 0, coldUnion = 0;
        var ix = 0;

        foreach (var path in busiest)
        {
            var dir = Path.Combine(workDir, $"live{ix++}");
            var once = Fingerprint(Replay(path, Path.Combine(dir, "once"), chunkSeconds: null, cold: false));
            var live = Fingerprint(Replay(path, Path.Combine(dir, "live"), options.LiveChunkSeconds, cold: false));
            var cold = Fingerprint(Replay(path, Path.Combine(dir, "cold"), options.LiveChunkSeconds, cold: true));

            common += once.Intersect(live).Count();
            union += once.Union(live).Count();
            coldCommon += once.Intersect(cold).Count();
            coldUnion += once.Union(cold).Count();

            if (!once.SetEquals(live))
            {
                foreach (var diff in once.Except(live).Take(5)) report.LiveDiffs.Add($"only in single pass: {diff}");
                foreach (var diff in live.Except(once).Take(5)) report.LiveDiffs.Add($"only when tailed:   {diff}");
            }
        }

        report.Counts["live.files"] = busiest.Count;
        report.Metrics["live.parity"] = union == 0 ? 1.0 : (double)common / union;
        report.Metrics["live.cold_parity"] = coldUnion == 0 ? 1.0 : (double)coldCommon / coldUnion;
    }

    /// <summary>
    /// Feeds one log file to a fresh store as Game.log, either whole or in passes of
    /// <paramref name="chunkSeconds"/> of log time, and returns the store.
    /// </summary>
    private static TrackerDb Replay(string source, string dir, int? chunkSeconds, bool cold)
    {
        Directory.CreateDirectory(dir);
        var db = new TrackerDb(Path.Combine(dir, "t.db"));
        db.Initialize();

        var live = Path.Combine(dir, "Game.log");
        var ingestor = new LogIngestor(db, dir);

        using (var fs = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var w = new StreamWriter(fs) { AutoFlush = true, NewLine = "\n" })
        {
            DateTimeOffset? passStart = null;
            foreach (var raw in new ByteLineReader().ReadLines(source))
            {
                if (chunkSeconds is { } seconds && LogLine.TryParse(raw.Text, out var line))
                {
                    passStart ??= line.Timestamp;
                    if (line.Timestamp - passStart.Value >= TimeSpan.FromSeconds(seconds))
                    {
                        if (cold) ingestor = new LogIngestor(db, dir);
                        ingestor.IngestAll();
                        passStart = line.Timestamp;
                    }
                }

                w.WriteLine(raw.Text);
            }
        }

        if (cold) ingestor = new LogIngestor(db, dir);
        ingestor.IngestAll();
        return db;
    }

    /// <summary>
    /// Everything the user could observe about a store: each move row as recorded, and each
    /// holding with its place, score and caveats. Ids and file paths are left out — they
    /// differ between runs without meaning anything.
    /// </summary>
    private static HashSet<string> Fingerprint(TrackerDb db)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        using (var cn = db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ts, move_type, item_class, COALESCE(item_geid, ''), amount,
                       src_raw, tgt_raw, COALESCE(caller, ''), COALESCE(result, '')
                FROM move ORDER BY ts, line_offset, item_ix
                """;
            using var r = cmd.ExecuteReader();
            var seen = new Dictionary<string, int>();
            while (r.Read())
            {
                var row = string.Join('|', Enumerable.Range(0, r.FieldCount)
                    .Select(i => Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)));
                var n = seen[row] = seen.GetValueOrDefault(row) + 1;
                set.Add($"move {row} #{n}");
            }
        }

        var resolver = HoldingResolver.Load(db, EndOfCorpus(db));
        foreach (var h in resolver.ResolveAll())
        {
            set.Add(
                $"holding {h.Where} | {h.ItemClass} | {h.Geid} | x{h.Quantity} | {h.Score:F3} | {string.Join("; ", h.Caveats)}");
        }

        return set;
    }

    private static Dictionary<string, long> MovesPerFile(TrackerDb db)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT s.path, COUNT(m.id) FROM session s LEFT JOIN move m ON m.session_id = s.id GROUP BY s.id";
        using var r = cmd.ExecuteReader();

        var map = new Dictionary<string, long>();
        while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }

    /// <summary>
    /// The newest line in the corpus. Staleness is measured from here rather than from now,
    /// so a report reads the same next month as it does today.
    /// </summary>
    private static DateTimeOffset EndOfCorpus(TrackerDb db)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT MAX(last_ts) FROM session";
        return cmd.ExecuteScalar() is string s ? ParseTs(s) : DateTimeOffset.UnixEpoch;
    }

    private static DateTimeOffset ParseTs(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    private static string Shorten(string line) => line.Length <= 260 ? line : line[..260] + "…";
}
