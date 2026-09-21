using InventoryTracker.Data;
using InventoryTracker.Ingest;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

public sealed class LogIngestorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public LogIngestorTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        _db.Initialize();
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by default, so the native handle to
        // tracker.db can outlive every SqliteConnection's Dispose() here. On Windows that
        // keeps the file locked, and deleting the directory right after fails with
        // "being used by another process" unless the pools are cleared first.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static string StoreLine(string ts, int requestNo) =>
        $"<{ts}> [Notice] <InventoryManagementRequest> Queued Request[{requestNo}] " +
        "Type[Store] for 'Pilot' [2001] Source Inventory[INVALID] " +
        "Target Inventory[204821708183:Location:4005457614]. " +
        "Source[NONE] amount[1] rank[0]. Target[NONE] amount[0] rank[0]. " +
        "Item[grin_multitool_01_red01_481104483777] action[none]";

    [Fact]
    public void Second_pass_does_not_rediscard_a_live_file_whose_inception_date_is_after_its_first_line()
    {
        // Regression: DiscardIfRotated compared the cutoff-filtered first_ts (only set for
        // lines at or after the inception date) against LogFileLocator.FirstTimestamp (the
        // file's true, unfiltered first line). Whenever the inception date fell after the
        // file's actual start, the two diverged forever, and every ingest pass mistook the
        // live log for a rotation, wiping and re-ingesting it from scratch.
        var path = Path.Combine(_dir, "Game.log");
        File.WriteAllText(
            path,
            "<2026-07-16T08:00:00.000Z> [Notice] <X> the file's true first line, before the cutoff\n" +
            StoreLine("2026-07-16T10:00:00.000Z", 1) + "\n");

        var inception = new DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.Zero);
        var ingestor = new LogIngestor(_db, _dir, inception);

        var first = ingestor.IngestAll();
        Assert.Equal(1, first.MovesInserted);

        var second = ingestor.IngestAll();
        Assert.Equal(0, second.MovesInserted);
    }
}
