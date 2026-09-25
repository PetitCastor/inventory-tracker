using System.Globalization;
using System.Runtime.CompilerServices;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Model;
using InventoryTracker.Resolve;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests.Playtest;

/// <summary>
/// Replays the live playtest of 2026-09-25 up to a chosen moment, through the real parser,
/// ingestor, ledger and resolver.
/// <para>
/// The fixtures under <c>Fixtures/Playtest20260925/logbackups</c> are that day's logs trimmed
/// to the lines the tracker reads, with the player's handle replaced and network identifiers
/// removed. Each step of the playtest was checked in game by the player, so what the ledger
/// should say at each moment is known — see the fixture's README.
/// </para>
/// <para>
/// A replay cut at <c>until</c> keeps every line up to and including that moment. The files
/// before the cut are rotated backups, as they were on the player's disk; the file the cut
/// falls in is either a backup too (the default) or, with <c>live</c>, the Game.log still
/// being written.
/// </para>
/// </summary>
internal sealed class PlaytestReplay : IDisposable
{
    /// <summary>Area18's local inventory, where every stored item in the playtest went.</summary>
    public static readonly Holding Area18 = new(InventoryKind.Location, "2273540638");

    public static readonly Holding Equipped = new(InventoryKind.Equipped, "");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-playtest-{Guid.NewGuid():N}");

    public TrackerDb Db { get; }

    public HoldingResolver Resolver { get; }

    public IReadOnlyList<ItemHolding> Holdings { get; }

    private PlaytestReplay(DateTimeOffset until, bool live)
    {
        var backups = Path.Combine(_dir, "logbackups");
        Directory.CreateDirectory(backups);

        var files = Directory.GetFiles(FixtureDir(), "*.log")
            .Select(path => (Path: path, Lines: File.ReadAllLines(path)))
            .Where(f => f.Lines.Length > 0 && StampOf(f.Lines[0]) is { } first && first <= until)
            .OrderBy(f => StampOf(f.Lines[0]))
            .ToList();

        // Discovery orders files by last write, so the copies are stamped in playing order.
        var written = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < files.Count; i++)
        {
            var (path, lines) = files[i];
            var kept = lines.TakeWhile(l => StampOf(l) is not { } ts || ts <= until);

            var target = live && i == files.Count - 1
                ? Path.Combine(_dir, "Game.log")
                : Path.Combine(backups, Path.GetFileName(path));

            File.WriteAllText(target, string.Join("\n", kept) + "\n");
            File.SetLastWriteTimeUtc(target, written = written.AddMinutes(1));
        }

        Db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        Db.Initialize();
        new LogIngestor(Db, _dir).IngestAll();

        Resolver = HoldingResolver.Load(Db, until);
        Holdings = Resolver.ResolveAll();
    }

    /// <summary>Replays the playtest up to and including <paramref name="utc"/> (a UTC time on 2026-09-25).</summary>
    public static PlaytestReplay Until(string utc, bool live = false) =>
        new(DateTimeOffset.Parse($"2026-09-25T{utc}Z", CultureInfo.InvariantCulture), live);

    /// <summary>The row for one named entity, or null when the ledger does not hold it anywhere.</summary>
    public ItemHolding? Named(string geid) => Holdings.SingleOrDefault(h => h.Geid == geid);

    /// <summary>Where the ledger has an entity, containers followed out to the place they sit.</summary>
    public HoldingLink? RootOf(string geid) => Named(geid)?.Root;

    /// <summary>Units of a class whose chain ends in <paramref name="root"/>, named and anonymous.</summary>
    public int UnitsAt(Holding root, string itemClass) => Holdings
        .Where(h => h.ItemClass == itemClass && h.Root is { } r && r.Kind == root.Kind && r.Key == root.Key)
        .Sum(h => h.Quantity);

    /// <summary>Named entities whose chain ends on the player — equipped, or carried in worn apparel.</summary>
    public IReadOnlySet<string> OnThePlayer() => Holdings
        .Where(h => h.Geid is not null && h.Root?.Kind == InventoryKind.Equipped)
        .Select(h => h.Geid!)
        .ToHashSet(StringComparer.Ordinal);

    public int TotalUnits => Holdings.Sum(h => h.Quantity);

    /// <summary>The first column of the first row a query returns, as text.</summary>
    public string? Scalar(string sql)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a pooled handle outlived the clear; the temp dir is harmless */ }
    }

    private static DateTimeOffset? StampOf(string line)
    {
        if (line.Length < 3 || line[0] != '<') return null;
        var end = line.IndexOf('>');
        return end > 1 && DateTimeOffset.TryParse(
            line.AsSpan(1, end - 1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)
            ? ts
            : null;
    }

    private static string FixtureDir([CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "..", "Fixtures", "Playtest20260925", "logbackups");
}
