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

    [Fact]
    public async Task An_unreadable_backup_is_reported_while_live_data_is_published()
    {
        var backupDir = Path.Combine(_dir, "logbackups");
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, "Game Build(12881860) 12 Oct 26 (10 00 00).log");
        var live = Path.Combine(_dir, "Game.log");
        var backupLine = Store.Replace("2026-08-27T00:40:00.000Z", "2026-10-12T10:30:00.000Z");
        var liveLine = Store.Replace("2026-08-27T00:40:00.000Z", "2026-10-13T11:40:00.000Z");
        File.WriteAllText(backup, backupLine + "\n");
        File.WriteAllText(live, liveLine + "\n");
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow);

        using (var blocker = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await _watcher.RebuildNowAsync();

            var incomplete = Assert.IsType<IngestPass>(_watcher.LastPass).Incomplete;
            Assert.Contains(Path.GetFileName(backup), incomplete!);
            Assert.Null(Assert.IsType<IngestPass>(_watcher.LastPass).Error);
            Assert.Equal(1, IngestHealth.Load(_db, _dir, null).IncompleteFiles);
            Assert.Equal(1, _state.Current.Health.Moves);
        }

        await _watcher.RunPassAsync(IngestTrigger.Poll);

        Assert.Null(Assert.IsType<IngestPass>(_watcher.LastPass).Error);
        Assert.Equal(0, IngestHealth.Load(_db, _dir, null).IncompleteFiles);
        Assert.Equal(2, _state.Current.Health.Moves);
    }

    [Fact]
    public async Task A_pass_that_reads_only_noise_still_refreshes_a_banner_it_made_wrong()
    {
        // The game logs in and loads for minutes before any inventory line. Holdings do not
        // change, but "the game has not written anything yet" stops being true, and Browse
        // reads the snapshot: without a rebuild that banner stayed up the whole time.
        File.WriteAllText(Path.Combine(_dir, "Game.log"), "");
        await _watcher.ResetAndRebuildAsync();
        Assert.Null(_state.Current.Health.NewestLogLine);

        Append("<2026-10-11T22:00:13.695Z> [Notice] <Legacy login response> User Login Success");
        await _watcher.RunPassAsync(IngestTrigger.Poll);

        Assert.True(Assert.IsType<IngestPass>(_watcher.LastPass).Rebuilt);
        Assert.NotNull(_state.Current.Health.NewestLogLine);
    }

    [Fact]
    public async Task A_pass_that_reads_noise_into_an_already_correct_banner_does_not_rebuild()
    {
        // The newest line's time moves on every pass while the game writes; rebuilding for it
        // alone would mean a full rebuild every two seconds.
        Append(Store);
        await _watcher.ResetAndRebuildAsync();

        Append("<2026-08-27T00:45:00.000Z> [Notice] <Legacy login response> User Login Success");
        await _watcher.RunPassAsync(IngestTrigger.Poll);

        Assert.False(Assert.IsType<IngestPass>(_watcher.LastPass).Rebuilt);
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_fail_the_pass()
    {
        // The loop runs passes inline; an exception escaping one would stop ExecuteAsync and
        // with it the host.
        _watcher.PassCompleted += () => throw new InvalidOperationException("page gone");
        Append(Store);

        await _watcher.RebuildNowAsync();

        var pass = Assert.IsType<IngestPass>(_watcher.LastPass);
        Assert.Null(pass.Error);
        Assert.NotEmpty(_state.Current.Holdings);
    }

    [Fact]
    public void A_snapshot_subscriber_that_throws_neither_fails_the_rebuild_nor_starves_the_others()
    {
        var reached = 0;
        _state.Changed += () => throw new InvalidOperationException("circuit gone");
        _state.Changed += () => reached++;

        _state.Rebuild();

        Assert.Equal(1, reached);
        Assert.NotEqual(DateTimeOffset.MinValue, _state.Current.BuiltAt);
    }
}
