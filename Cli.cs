using LogParser.Data;
using LogParser.Ingest;
using LogParser.Naming;
using LogParser.Resolve;
using Microsoft.Data.Sqlite;

namespace LogParser;

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var logDir = LogFileLocator.DefaultLogDir;
        var dbPath = TrackerDb.DefaultPath;
        var reset = false;
        var trace = (string?)null;
        var find = (string?)null;
        var holdings = false;
        var refreshNames = false;
        var checkNames = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log-dir" when i + 1 < args.Length: logDir = args[++i]; break;
                case "--db" when i + 1 < args.Length: dbPath = args[++i]; break;
                case "--trace" when i + 1 < args.Length: trace = args[++i]; break;
                case "--find" when i + 1 < args.Length: find = args[++i]; break;
                case "--holdings": holdings = true; break;
                case "--refresh-names": refreshNames = true; break;
                case "--names": checkNames = true; break;
                case "--reset": reset = true; break;
                case "--scan": break;
                case "--help" or "-h": PrintUsage(); return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        if (!Directory.Exists(logDir))
        {
            Console.Error.WriteLine($"Log directory not found: {logDir}");
            return 1;
        }

        var db = new TrackerDb(dbPath);
        db.Initialize();
        if (reset) db.ResetIngest();

        Console.WriteLine($"Log dir : {logDir}");
        Console.WriteLine($"Database: {db.Path}");
        Console.WriteLine($"Cutoff  : {InventoryEventParser.Cutoff:yyyy-MM-dd} (4.9)");
        Console.WriteLine();

        var started = DateTime.UtcNow;
        var stats = new LogIngestor(db, logDir).IngestAll();
        var elapsed = DateTime.UtcNow - started;

        Console.WriteLine($"Files discovered   : {stats.FilesSeen}");
        Console.WriteLine($"Files parsed       : {stats.FilesParsed}");
        Console.WriteLine($"Lines read         : {stats.LinesRead:N0}");
        Console.WriteLine($"Moves inserted     : {stats.MovesInserted:N0}");
        Console.WriteLine($"Completions applied: {stats.CompletionsApplied:N0}");
        Console.WriteLine($"Container IDs seen : {stats.ContainersIdentified:N0}");
        Console.WriteLine($"Location bindings  : {stats.LocationsNamed:N0}");
        Console.WriteLine($"Location conflicts : {stats.LocationConflicts:N0}");
        Console.WriteLine($"Batch moves fanned : {stats.BatchMovesExpanded:N0}");
        Console.WriteLine($"Geids recovered    : {stats.GeidsRecovered:N0}");
        Console.WriteLine($"Worn sightings     : {stats.AttachmentsSeen:N0}");
        Console.WriteLine($"Place evidence     : {stats.PlaceEvidence:N0}");
        Console.WriteLine($"Elapsed            : {elapsed.TotalSeconds:F1}s");
        Console.WriteLine();

        Summarize(db);

        if (refreshNames) await RefreshNamesAsync(db);
        if (checkNames) CheckNameCoverage(db);
        if (trace is not null) Trace(db, trace);
        if (holdings) PrintHoldings(db, null);
        if (find is not null) PrintHoldings(db, find);

        return 0;
    }

    private static async Task RefreshNamesAsync(TrackerDb db)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SCLogParser/1.0 (inventory tracker)");

        Console.WriteLine();
        Console.WriteLine("-- refreshing item catalogue from star-citizen.wiki --");

        var progress = new Progress<string>(s => Console.WriteLine($"  {s}"));
        var count = await new WikiNameService(db, http).RefreshAsync(progress);

        Console.WriteLine($"  cached {count:N0} items");
    }

    /// <summary>Reports how much of what we actually observed the catalogue can name.</summary>
    private static void CheckNameCoverage(TrackerDb db)
    {
        using var http = new HttpClient();
        var names = new WikiNameService(db, http);

        using var cn = db.Open();
        var classes = Rows(cn, "SELECT DISTINCT item_class, '' FROM move ORDER BY item_class")
            .Select(r => r.Item1)
            .ToList();

        var resolved = classes.Select(names.Resolve).ToList();
        var byMatch = resolved.GroupBy(r => r.Match).ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine();
        Console.WriteLine($"-- name coverage over {classes.Count} observed classes --");
        foreach (var kind in Enum.GetValues<NameMatch>())
        {
            Console.WriteLine($"  {kind,-8} {byMatch.GetValueOrDefault(kind),4}");
        }

        var missing = resolved.Where(r => r.Match == NameMatch.None).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  unresolved:");
            foreach (var m in missing) Console.WriteLine($"    {m.ClassName}");
        }

        Console.WriteLine();
        Console.WriteLine("  samples:");
        foreach (var r in resolved.Where(r => r.Match != NameMatch.None).Take(12))
        {
            Console.WriteLine($"    {r.ClassName,-44} -> {r.Display}  ({r.Match})");
        }
    }

    /// <summary>Groups every inferred placement by where it ends up â€” the Find view, in text.</summary>
    private static void PrintHoldings(TrackerDb db, string? filter)
    {
        var resolved = HoldingResolver.Load(db).ResolveAll();

        if (filter is not null)
        {
            resolved = resolved
                .Where(h => h.ItemClass.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        Console.WriteLine();
        Console.WriteLine(filter is null
            ? $"-- holdings ({resolved.Sum(h => h.Quantity)} units in {resolved.Count} lines) --"
            : $"-- holdings matching '{filter}' ({resolved.Sum(h => h.Quantity)} units) --");

        // Group by the root's identity, not its label: several distinct SCU boxes share
        // the class name "Carryable_TBO_InventoryContainer_2SCU" and are different places.
        foreach (var group in resolved
                     .GroupBy(h => (h.Root?.Kind, h.Root?.Key))
                     .OrderByDescending(g => g.Count()))
        {
            var root = group.First().Root;
            var heading = root is null ? "unknown"
                : root.Key.Length > 0 ? $"{root.Label} [{root.Key}]"
                : root.Label;

            Console.WriteLine();
            Console.WriteLine($"  {heading}  ({group.Count()})");

            foreach (var h in group.OrderBy(h => h.ItemClass))
            {
                Console.WriteLine(
                    $"    {h.ItemClass,-44} {h.Where,-52} {h.Confidence,-6} {h.LastMove:yyyy-MM-dd HH:mm}");

                foreach (var caveat in h.Caveats)
                {
                    Console.WriteLine($"      ! {caveat}");
                }
            }
        }
    }

    private static void PrintUsage() => Console.WriteLine("""
        Usage: LogParser [--scan] [options]

          --log-dir <path>   Star Citizen LIVE folder (default: E:\Games\StarCitizen\LIVE)
          --db <path>        SQLite store (default: %LOCALAPPDATA%\SCLogParser\tracker.db)
          --reset            Discard ingested logs and re-parse from scratch
          --holdings         Print every item's inferred location, grouped
          --find <text>      As --holdings, limited to item classes containing <text>
          --trace <geid>     Print the movement history of one item instance
          --refresh-names    Download the star-citizen.wiki item catalogue (~62 requests)
          --names            Report how many observed classes the catalogue can name
        """);

    private static void Summarize(TrackerDb db)
    {
        using var cn = db.Open();

        Console.WriteLine("-- store contents --");
        Console.WriteLine($"Sessions          : {Scalar(cn, "SELECT COUNT(*) FROM session"):N0}");
        Console.WriteLine($"Moves             : {Scalar(cn, "SELECT COUNT(*) FROM move"):N0}");
        Console.WriteLine($"  succeeded       : {Scalar(cn, "SELECT COUNT(*) FROM move WHERE result IN ('Succeed','succeed')"):N0}");
        Console.WriteLine($"  unconfirmed     : {Scalar(cn, "SELECT COUNT(*) FROM move WHERE result IS NULL"):N0}");
        Console.WriteLine($"Distinct items    : {Scalar(cn, "SELECT COUNT(DISTINCT item_geid) FROM move"):N0}");
        Console.WriteLine($"Distinct classes  : {Scalar(cn, "SELECT COUNT(DISTINCT item_class) FROM move"):N0}");
        Console.WriteLine($"Location names    : {Scalar(cn, "SELECT COUNT(*) FROM location_name"):N0}");
        Console.WriteLine($"Known containers  : {Scalar(cn, "SELECT COUNT(*) FROM container_class"):N0}");

        var containersUsed = Scalar(cn,
            "SELECT COUNT(DISTINCT tgt_key) FROM move WHERE tgt_kind = 'Container'");
        var containersKnown = Scalar(cn, """
            SELECT COUNT(DISTINCT m.tgt_key) FROM move m
            JOIN container_class c ON c.geid = m.tgt_key
            WHERE m.tgt_kind = 'Container'
            """);
        Console.WriteLine($"Containers in use : {containersUsed:N0} ({containersKnown:N0} identified)");

        Console.WriteLine($"Distinct instances: {Scalar(cn, "SELECT COUNT(DISTINCT item_geid) FROM move WHERE item_geid IS NOT NULL"):N0}");
        Console.WriteLine($"  named moves     : {Scalar(cn, "SELECT COUNT(*) FROM move WHERE item_geid IS NOT NULL"):N0}");
        Console.WriteLine($"  class-only moves: {Scalar(cn, "SELECT COUNT(*) FROM move WHERE item_geid IS NULL"):N0}");
        Console.WriteLine($"Worn entities     : {Scalar(cn, "SELECT COUNT(*) FROM attachment"):N0}");

        Console.WriteLine();
        Console.WriteLine("-- moves by type --");
        foreach (var (type, count) in Rows(cn,
            "SELECT move_type, COUNT(*) FROM move GROUP BY move_type ORDER BY COUNT(*) DESC"))
        {
            Console.WriteLine($"  {type,-14} {count,6}");
        }

        Console.WriteLine();
        Console.WriteLine("-- places --");
        foreach (var place in PlaceCatalog.Load(db).Places)
        {
            var flags = (place.NameVerified ? "" : " [name unverified]") +
                        (place.SystemVerified ? "" : " [system inferred]");
            Console.WriteLine($"  {place.System,-14} {place.Name,-38} {string.Join(",", place.AliasIds)}{flags}");
            if (place.RawName is { } raw && raw != place.Name) Console.WriteLine($"  {"",-14} logged internally as {raw}");
        }
    }

    /// <summary>Prints one item instance's full movement history â€” the audit view.</summary>
    private static void Trace(TrackerDb db, string geid)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT m.ts, m.move_type, m.tgt_raw, m.result,
                   COALESCE(l.name, ''), COALESCE(c.class_name, ''), m.item_class
            FROM move m
            LEFT JOIN location_name  l ON m.tgt_kind = 'Location'  AND l.location_id = m.tgt_key
            LEFT JOIN container_class c ON m.tgt_kind = 'Container' AND c.geid       = m.tgt_key
            WHERE m.item_geid = $g
            ORDER BY m.ts
            """;
        cmd.Parameters.AddWithValue("$g", geid);

        Console.WriteLine();
        Console.WriteLine($"-- trace {geid} --");

        using var r = cmd.ExecuteReader();
        var any = false;
        while (r.Read())
        {
            any = true;
            var target = r.GetString(2);
            var label = r.GetString(4) is { Length: > 0 } loc ? loc
                      : r.GetString(5) is { Length: > 0 } cls ? cls
                      : "(unidentified)";
            var result = r.IsDBNull(3) ? "unconfirmed" : r.GetString(3);
            Console.WriteLine($"  {r.GetString(0)[..19]}  {r.GetString(1),-11} -> {target,-34} {label,-38} [{result}]");
        }

        if (!any) Console.WriteLine("  no moves recorded");
    }

    private static long Scalar(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<(string, string)> Rows(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();

        var rows = new List<(string, string)>();
        while (r.Read()) rows.Add((r.GetValue(0)?.ToString() ?? "", r.GetValue(1)?.ToString() ?? ""));
        return rows;
    }
}

