using InventoryTracker.App;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Naming;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests;

public sealed class LogWatchServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;
    private readonly TrackerOptions _options;
    private readonly TrackerState _state;
    private readonly LogWatchService _watcher;

    public LogWatchServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        _db.Initialize();

        _options = new TrackerOptions { LogDir = _dir, DatabasePath = _db.Path };
        var names = new WikiNameService(_db, new HttpClient());
        _state = new TrackerState(_db, names, _options);
        _watcher = new LogWatchService(
            _options, _db, _state, names, new PlaceNameService(_db, _options, new HttpClient()),
            NullLogger<LogWatchService>.Instance);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private const string Store =
        "<2026-08-27T00:40:00.000Z> [Notice] <InventoryManagementRequest> Queued Request[1] " +
        "Type[Store] for 'Pilot' [2001] Source Inventory[INVALID] " +
        "Target Inventory[204821708183:Location:4005457614]. " +
        "Source[NONE] amount[1] rank[0]. Target[NONE] amount[0] rank[0]. " +
        "Item[grin_multitool_01_red01_481104483777] action[none]";

    private void Append(string line) => File.AppendAllText(Path.Combine(_dir, "Game.log"), line + "\n");

    [Fact]
    public async Task A_reset_that_reads_nothing_still_replaces_the_snapshot()
    {
        // Regression: a pass only published a snapshot when it read something new. Moving
        // the wipe date past every line wiped the store, re-read nothing, and left the UI
        // showing the items that had just been deleted.
        Append(Store);
        await _watcher.ResetAndRebuildAsync();
        Assert.NotEmpty(_state.Current.Holdings);

        _options.InceptionDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await _watcher.ResetAndRebuildAsync();

        Assert.Empty(_state.Current.Holdings);
        Assert.Equal(0, _state.Current.Health.Moves);
    }

    [Fact]
    public async Task Every_pass_is_recorded_even_when_it_finds_nothing()
    {
        // Without this, "the logs were never read" and "they were read and held nothing new"
        // both look like an empty page.
        var completed = 0;
        _watcher.PassCompleted += () => completed++;

        await _watcher.RebuildNowAsync();

        var empty = Assert.IsType<IngestPass>(_watcher.LastPass);
        Assert.Equal(IngestTrigger.Manual, empty.Trigger);
        Assert.Equal(0, empty.MovesInserted);
        Assert.Null(empty.Error);
        Assert.Null(_watcher.LastMovesAt);

        Append(Store);
        await _watcher.RebuildNowAsync();

        var found = Assert.IsType<IngestPass>(_watcher.LastPass);
        Assert.Equal(1, found.MovesInserted);
        Assert.True(found.Rebuilt);
        Assert.NotNull(_watcher.LastMovesAt);
        Assert.Equal(2, completed);
    }
}
