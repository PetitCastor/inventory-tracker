using InventoryTracker.App;
using InventoryTracker.Data;
using InventoryTracker.Naming;
using InventoryTracker.Resolve;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

public sealed class TrackerStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"itrk-state-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public TrackerStateTests()
    {
        Directory.CreateDirectory(_directory);
        _db = new TrackerDb(Path.Combine(_directory, "tracker.db"));
        _db.Initialize();
        ExecuteSql("INSERT INTO session(id, path) VALUES(1, 'generation.log')");
        ExecuteSql("INSERT INTO move(id, session_id, line_offset, request_no, ts, move_type, item_class, amount, src_kind, src_key, tgt_kind, tgt_key, tgt_raw, result) " +
                   "VALUES(1, 1, 1, 1, '2026-10-13T00:00:00Z', 'Store', 'item', 1, 'Location', 'source', 'Location', 'old-id', 'old-id', 'succeed')");
        ExecuteSql("INSERT INTO location_name(location_id, name) VALUES('old-id', 'old-generation')");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Load_keeps_location_names_from_the_same_generation_as_the_moves()
    {
        var resolver = HoldingResolver.Load(_db, asOf: null, afterMovesRead: CommitNextGeneration);

        Assert.Equal("old-generation", resolver.Places.ByLocationId("old-id")?.RawName);
        Assert.Null(resolver.Places.ByLocationId("new-id"));
    }

    [Fact]
    public void Load_after_commit_observes_the_complete_new_generation()
    {
        CommitNextGeneration();

        var resolver = HoldingResolver.Load(_db);

        Assert.Null(resolver.Places.ByLocationId("old-id"));
        Assert.Equal("new-generation", resolver.Places.ByLocationId("new-id")?.RawName);
        Assert.Single(resolver.Moves);
    }

    [Fact]
    public void Load_keeps_one_generation_while_reset_commits_between_table_reads()
    {
        var resolver = HoldingResolver.Load(_db, asOf: null, afterMovesRead: _db.ResetIngest);

        Assert.Equal("old-generation", resolver.Places.ByLocationId("old-id")?.RawName);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM move"));
    }

    [Fact]
    public void Load_closes_the_read_transaction_before_the_in_memory_replay()
    {
        var resolver = HoldingResolver.Load(
            _db,
            asOf: null,
            afterMovesRead: CommitNextGeneration,
            beforeReplay: CommitFinalGeneration);

        Assert.Equal("old-generation", resolver.Places.ByLocationId("old-id")?.RawName);
        Assert.Equal("final-generation", Scalar("SELECT name FROM location_name WHERE location_id = 'final-id'"));
    }

    [Fact]
    public void Rebuild_keeps_resolver_and_health_from_the_same_generation_when_reset_commits()
    {
        var state = new TrackerState(_db, new WikiNameService(_db, new HttpClient()), new TrackerOptions());

        state.Rebuild(afterOptionsCaptured: null, afterResolverLoaded: _db.ResetIngest, beforePublish: null);

        Assert.Equal((long)state.Current.Resolver!.Moves.Count, state.Current.Health.Moves);
    }

    [Fact]
    public async Task Rebuild_publishes_one_complete_snapshot_and_uses_its_captured_options()
    {
        var options = new TrackerOptions
        {
            LogDir = "old-log-directory",
            InceptionDate = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        };
        var state = new TrackerState(_db, new WikiNameService(_db, new HttpClient()), options);
        state.Rebuild();
        var previous = state.Current;
        using var beforePublish = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();

        var rebuilding = Task.Run(() => state.Rebuild(
            afterOptionsCaptured: () =>
            {
                options.LogDir = "new-log-directory";
                options.InceptionDate = new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
            },
            afterResolverLoaded: null,
            beforePublish: () =>
            {
                beforePublish.Set();
                publish.Wait();
            }));

        try
        {
            Assert.True(beforePublish.Wait(TimeSpan.FromSeconds(5)));
            Assert.Same(previous, state.Current);
        }
        finally
        {
            publish.Set();
        }
        await rebuilding;

        var current = state.Current;
        Assert.NotSame(previous, current);
        Assert.NotNull(current.Resolver);
        Assert.Equal("old-log-directory", current.Health.LogDir);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), current.Health.InceptionDate);
    }

    [Fact]
    public async Task Rebuild_does_not_capture_log_source_options_from_different_generations()
    {
        var oldDate = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var newDate = new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
        var options = new TrackerOptions { LogDir = "old-log-directory", InceptionDate = oldDate };
        var state = new TrackerState(_db, new WikiNameService(_db, new HttpClient()), options);
        using var logDirCaptured = new ManualResetEventSlim();
        using var writerContended = new ManualResetEventSlim();
        using var writerFinished = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            Assert.True(logDirCaptured.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                options.SetLogSource("new-log-directory", newDate, afterContention: writerContended.Set);
            }
            finally
            {
                writerFinished.Set();
            }
        });

        var rebuilding = Task.Run(() => state.Rebuild(
            afterOptionsCaptured: () => Assert.True(writerFinished.Wait(TimeSpan.FromSeconds(5))),
            afterResolverLoaded: null,
            beforePublish: null,
            afterLogDirCaptured: () =>
            {
                logDirCaptured.Set();
                Assert.True(writerContended.Wait(TimeSpan.FromSeconds(5)));
            }));

        await Task.WhenAll(writer, rebuilding);

        Assert.Equal("old-log-directory", state.Current.Health.LogDir);
        Assert.Equal(oldDate, state.Current.Health.InceptionDate);
        Assert.Equal("new-log-directory", options.LogDir);
        Assert.Equal(newDate, options.InceptionDate);
    }

    private void CommitNextGeneration() => CommitGeneration("new-id", "new-generation", 2);

    private void CommitFinalGeneration(SqliteConnection cn) =>
        CommitGeneration(cn, "final-id", "final-generation", 3);

    private void CommitGeneration(string id, string name, long moveId)
    {
        using var cn = _db.Open();
        CommitGeneration(cn, id, name, moveId);
    }

    private static void CommitGeneration(SqliteConnection cn, string id, string name, long moveId)
    {
        using var transaction = cn.BeginTransaction();
        using var cmd = cn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            DELETE FROM move;
            INSERT INTO move(id, session_id, line_offset, request_no, ts, move_type, item_class, amount, src_kind, src_key, tgt_kind, tgt_key, tgt_raw, result)
            VALUES({moveId}, 1, {moveId}, {moveId}, '2026-10-13T00:00:01Z', 'Store', 'item', 1, 'Location', 'source', 'Location', '{id}', '{id}', 'succeed');
            DELETE FROM location_name;
            INSERT INTO location_name(location_id, name) VALUES('{id}', '{name}');
            """;
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    private void ExecuteSql(string sql)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string? Scalar(string sql)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
