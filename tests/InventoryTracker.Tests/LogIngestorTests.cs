using System.Text.Json;
using InventoryTracker.App;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Naming;
using InventoryTracker.Resolve;
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

    private string LivePath => Path.Combine(_dir, "Game.log");

    /// <summary>Appends lines to the live log, the way the game grows it between passes.</summary>
    private void Append(params string[] lines) => File.AppendAllText(LivePath, string.Join("\n", lines) + "\n");

    private string? Scalar(string sql)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void ExecuteSql(string sql)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void An_unreadable_backup_does_not_starve_live_and_is_retried_without_duplicates()
    {
        var backups = Path.Combine(_dir, "logbackups");
        Directory.CreateDirectory(backups);
        var backup = Path.Combine(backups, "Game Build(12881860) 12 Oct 26 (10 00 00).log");
        var live = LivePath;
        File.WriteAllText(backup, StoreLine("2026-10-12T10:00:00.000Z", 1) + "\n");
        File.WriteAllText(live, StoreLine("2026-10-13T11:40:00.000Z", 2) + "\n");
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow);

        using (var blocker = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ingestor = new LogIngestor(_db, _dir);
            IngestStats? first = null;
            IngestStats? second = null;
            var firstError = Record.Exception(() => first = ingestor.IngestAll());
            var secondError = Record.Exception(() => second = ingestor.IngestAll());

            Assert.Null(firstError);
            Assert.Null(secondError);
            Assert.Contains(Path.GetFileName(backup)!, Assert.Single(first!.FileErrors));
            Assert.Contains(Path.GetFileName(backup)!, Assert.Single(second!.FileErrors));
            Assert.Equal(1, Convert.ToInt32(Scalar("SELECT COUNT(*) FROM session WHERE path = '" + live.Replace("'", "''") + "'")));
            Assert.Equal(1, Convert.ToInt32(Scalar("SELECT COUNT(*) FROM move")));
            Assert.Equal("0", Scalar("SELECT byte_offset FROM session WHERE path = '" + backup.Replace("'", "''") + "'"));
            Assert.Equal(1, IngestHealth.Load(_db, _dir, null).IncompleteFiles);
        }

        var resumed = new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal(1, resumed.MovesInserted);
        Assert.Equal(2, Convert.ToInt32(Scalar("SELECT COUNT(*) FROM move")));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM session WHERE complete = 1"));
        Assert.Equal(0, IngestHealth.Load(_db, _dir, null).IncompleteFiles);
    }

    [Fact]
    public void A_database_integrity_error_still_propagates_from_ingestion()
    {
        Append(StoreLine("2026-10-13T11:40:00.000Z", 2));
        ExecuteSql("CREATE TRIGGER l09_reject_move BEFORE INSERT ON move BEGIN SELECT RAISE(ABORT, 'l09 global integrity'); END;");

        var error = Assert.Throws<SqliteException>(() => new LogIngestor(_db, _dir).IngestAll());

        Assert.Contains("l09 global integrity", error.Message);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("0", Scalar("SELECT byte_offset FROM session WHERE path = '" + LivePath.Replace("'", "''") + "'"));
    }

    private const string Arrive =
        "<2026-08-27T00:39:21.956Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
        "Landing [0] -> [2273540638]. Location [0] -> [2273540638]. Pending [0]";

    private const string OpenSuit =
        "<2026-08-27T00:42:01.289Z> [Notice] <Inventory Token Flow> Requesting access token for " +
        "User[Pilot, 204821708183] on Inventory[cds_combat_superheavy_suit_01_04_01_783810961476, 783810961476]";

    [Fact]
    public void A_store_line_on_its_own_is_reported_as_a_change()
    {
        // The watcher only publishes a snapshot when a pass reports a change. A store line
        // inserted a move row without counting it, so a pass that read nothing else left the
        // UI without the item it had just placed. It is not a new move for the player, though:
        // it usually doubles the Queued line of the same action.
        var ingestor = new LogIngestor(_db, _dir);
        Append(
            "<2026-08-27T00:41:00.000Z> [Notice] <OnInventoryStoreItem> " +
            "Entity[grin_multitool_01_red01_481104483777 - Class(grin_multitool_01_red01) - x] " +
            "Inventory[204821708183:Location:4005457614]");

        var stats = ingestor.IngestAll();

        Assert.Equal(1, stats.StoresRecorded);
        Assert.Equal(0, stats.MovesInserted);
        Assert.True(stats.ChangedHoldings);
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
    }

    [Fact]
    public void A_container_opened_in_a_later_pass_is_placed_where_an_earlier_pass_saw_the_player_arrive()
    {
        // The game writes the location once, on arrival. The watcher runs a pass every second
        // or so, so the container is almost always opened in a later pass than the arrival.
        var ingestor = new LogIngestor(_db, _dir);
        Append(Arrive);
        ingestor.IngestAll();

        Append(OpenSuit);
        ingestor.IngestAll();

        Assert.Equal("2273540638", Scalar("SELECT last_loc_id FROM container_class WHERE geid = '783810961476'"));
    }

    [Fact]
    public void The_players_location_survives_a_restart_mid_session()
    {
        Append(Arrive);
        new LogIngestor(_db, _dir).IngestAll();

        // A fresh ingestor has no carried state; only the store remembers where the player is.
        Append(OpenSuit);
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("2273540638", Scalar("SELECT last_loc_id FROM container_class WHERE geid = '783810961476'"));
    }

    [Fact]
    public void An_entity_spawn_in_the_next_pass_still_names_the_move_it_fulfils()
    {
        // The spawn that names an equipped item lands ~100ms after the completion line, so a
        // pass boundary between them is routine during play.
        var ingestor = new LogIngestor(_db, _dir);
        Append(
            "<2026-08-27T00:41:57.774Z> [Notice] <Add Inventory Management Move> New Request[11] Player[Pilot] " +
            "Type[Interaction] SourceInventory[204821708183:Location:2273540638] TargetInventory[INVALID] " +
            "ItemClass[cds_combat_superheavy_suit_01_04_01] StoredEntity[NULL] LocallyDetached[No] " +
            "LocalAttached[NULL, Body_ItemPort:Armor_Undersuit] PendingMoves[1, ] " +
            "Caller[CSCLocalPlayerPersonalThoughtComponent::AttachItem]",
            "<2026-08-27T00:41:57.775Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[11] Type[Interaction] " +
            "for 'Pilot' [204821708183] Source Inventory[204821708183:Location:2273540638] Target Inventory[INVALID]. " +
            "Source[cds_combat_superheavy_suit_01_04_01] amount[1] rank[amrgzkwktuhbn]. Target[NULL] amount[0] rank[]. " +
            "Item[NONE] action[None]. RequestInProgress[0] CurrentProcess[]",
            "<2026-08-27T00:42:00.703Z> [Notice] <Inventory Request Completed> Request[11] Player[Pilot] " +
            "Result[succeed] Elapsed[2.929414] PendingMoves[2]");
        Assert.Equal(1, ingestor.IngestAll().MovesInserted);

        Append(
            "<2026-08-27T00:42:00.819Z> [Notice] <UnstowPendingEntities> Unstow Request[11] for 'Pilot' " +
            "[204821708183] finalized spawn of 'cds_combat_superheavy_suit_01_04_01_783810961476' [783810961476], 0 remaining");
        ingestor.IngestAll();

        Assert.Equal("783810961476", Scalar("SELECT item_geid FROM move"));
        Assert.Equal("Equipped", Scalar("SELECT tgt_kind FROM move"));
    }

    [Fact]
    public void Reusing_a_request_number_consumes_each_spawn_for_its_own_occurrence()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(
            AddMove("10:00:00.000", 11, cls), QueuedMove("10:00:00.001", 11, cls),
            CompletedMove("10:00:01.000", 11), SpawnMove("10:00:02.000", 11, cls, "783810961476"),
            AddMove("10:00:03.000", 11, cls), QueuedMove("10:00:03.001", 11, cls),
            CompletedMove("10:00:04.000", 11), SpawnMove("10:00:05.000", 11, cls, "783810961477"));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("783810961476,783810961477", Scalar("SELECT group_concat(item_geid, ',') FROM (SELECT item_geid FROM move ORDER BY id)"));
    }

    [Fact]
    public void Replaying_a_consumed_spawn_does_not_assign_it_to_a_reused_occurrence()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var first = new[] { AddMove("10:00:00.000", 11, cls), QueuedMove("10:00:00.001", 11, cls),
            CompletedMove("10:00:01.000", 11), SpawnMove("10:00:02.000", 11, cls, "783810961476") };
        Append(first);
        new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("783810961476", Scalar("SELECT item_geid FROM move"));

        ExecuteSql("UPDATE session SET byte_offset = " + System.Text.Encoding.UTF8.GetByteCount(first[0] + "\n"));
        Append(AddMove("10:00:03.000", 11, cls), QueuedMove("10:00:03.001", 11, cls),
            CompletedMove("10:00:04.000", 11), SpawnMove("10:00:05.000", 11, cls, "783810961477"));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("783810961476,783810961477", Scalar("SELECT group_concat(item_geid, ',') FROM (SELECT item_geid FROM move ORDER BY id)"));
    }

    [Theory]
    [InlineData("783810961476")]
    [InlineData("783810961478")]
    public void Replaying_a_move_does_not_consume_a_new_spawn_waiting_for_the_next_occurrence(string nextGeid)
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var first = new[] { AddMove("10:00:00.000", 11, cls), QueuedMove("10:00:00.001", 11, cls),
            CompletedMove("10:00:01.000", 11), SpawnMove("10:00:02.000", 11, cls, "783810961476") };
        Append(first);
        new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("783810961476", Scalar("SELECT item_geid FROM move"));

        Append(SpawnMove("10:00:03.000", 11, cls, nextGeid));
        new LogIngestor(_db, _dir).IngestAll();
        ExecuteSql("UPDATE session SET byte_offset = " + System.Text.Encoding.UTF8.GetByteCount(first[0] + "\n"));
        Append(AddMove("10:00:04.000", 11, cls), QueuedMove("10:00:04.001", 11, cls),
            CompletedMove("10:00:05.000", 11));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal(nextGeid,
            Scalar("SELECT item_geid FROM move WHERE id = (SELECT MAX(id) FROM move)"));
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM move"));
    }

    [Theory]
    [InlineData("offset", "disconnect")]
    [InlineData("version", "disconnect")]
    [InlineData("offset", "counter")]
    [InlineData("version", "counter")]
    public void An_incompatible_snapshot_does_not_reopen_a_closed_generation(string invalidation, string boundary)
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:40:00.000", 23, cls), QueuedMove("10:40:00.001", 23, cls));
        if (boundary == "disconnect")
            Append("<2026-10-13T10:40:01.000Z> [Notice] <Channel Disconnected> cause=30016 reason=\"Remote Disconnect\" frame=1");
        else Append(AddMove("10:40:01.000", 1, cls), QueuedMove("10:40:01.001", 1, cls));
        new LogIngestor(_db, _dir).IngestAll();
        ExecuteSql(invalidation == "offset"
            ? "UPDATE ingest_correlation SET byte_offset = byte_offset - 1"
            : "UPDATE ingest_correlation SET state_version = state_version + 1");
        Append(SpawnMove("10:40:02.000", 23, cls, "783810961490"));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("<NULL>", Scalar("SELECT COALESCE(item_geid, '<NULL>') FROM move WHERE request_no = 23"));
    }

    [Theory]
    [InlineData("unknown-payload-version")]
    [InlineData("inconsistent-generation")]
    [InlineData("missing-snapshot")]
    public void Conservative_snapshot_fallback_keeps_old_spawn_fenced_and_allows_late_lost_completion(string invalidation)
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:40:00.000", 23, cls), QueuedMove("10:40:00.001", 23, cls),
            "<2026-10-13T10:40:01.000Z> [Notice] <Channel Disconnected> cause=30016 reason=\"Remote Disconnect\" frame=1");
        new LogIngestor(_db, _dir).IngestAll();
        ExecuteSql(invalidation switch
        {
            "unknown-payload-version" => "UPDATE ingest_correlation SET state_json = json_set(state_json, '$.Version', 999)",
            "inconsistent-generation" => "UPDATE ingest_correlation SET state_json = json_set(state_json, '$.Generation', 999)",
            _ => "DELETE FROM ingest_correlation",
        });

        Append(SpawnMove("10:40:02.000", 23, cls, "783810961490"));
        var spawnPass = new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("<NULL>", Scalar("SELECT COALESCE(item_geid, '<NULL>') FROM move WHERE request_no = 23"));
        Assert.False(spawnPass.ChangedHoldings);

        Append(CompletedMove("10:40:03.000", 23));
        var completionPass = new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("succeed", Scalar("SELECT result FROM move WHERE request_no = 23"));
        Assert.True(completionPass.ChangedHoldings);
        Assert.Equal(1, completionPass.CompletionsApplied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replaying_an_old_completion_does_not_confirm_a_new_occurrence(bool retryAfterSnapshotWriteFailure)
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var completedOccurrence = new[]
        {
            AddMove("10:00:00.000", 11, cls), QueuedMove("10:00:00.001", 11, cls),
            CompletedMove("10:00:01.000", 11),
        };
        Append(completedOccurrence);
        Append(AddMove("10:00:03.000", 11, cls), QueuedMove("10:00:03.001", 11, cls));
        new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("succeed|<NULL>", Scalar(
            "SELECT group_concat(result, '|') FROM (SELECT COALESCE(result, '<NULL>') AS result FROM move ORDER BY id)"));

        ExecuteSql("UPDATE session SET byte_offset = " + System.Text.Encoding.UTF8.GetByteCount(
            completedOccurrence[0] + "\n" + completedOccurrence[1] + "\n"));
        var ingestor = new LogIngestor(_db, _dir);
        if (retryAfterSnapshotWriteFailure)
        {
            var snapshot = Scalar("SELECT state_json FROM ingest_correlation");
            ExecuteSql("CREATE TRIGGER l04_r3_abort BEFORE UPDATE ON ingest_correlation BEGIN SELECT RAISE(ABORT, 'l04 r3 retry'); END");
            Assert.Contains("l04 r3 retry", Assert.Throws<SqliteException>(() => ingestor.IngestAll()).Message);
            Assert.Equal(snapshot, Scalar("SELECT state_json FROM ingest_correlation"));
            Assert.Equal("<NULL>", Scalar("SELECT COALESCE(result, '<NULL>') FROM move ORDER BY id DESC LIMIT 1"));
            ExecuteSql("DROP TRIGGER l04_r3_abort");
        }

        ingestor.IngestAll();
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("succeed|<NULL>", Scalar(
            "SELECT group_concat(result, '|') FROM (SELECT COALESCE(result, '<NULL>') AS result FROM move ORDER BY id)"));
    }

    [Fact]
    public void Rewinding_after_add_preserves_the_pending_attach_caller()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var add = AddMove("10:00:00.000", 11, cls);
        Append(add, "<2026-10-13T10:00:00.000Z> [Notice] <X> harmless checkpoint line");
        new LogIngestor(_db, _dir).IngestAll();
        Assert.Contains("AttachItem", Scalar("SELECT state_json FROM ingest_correlation"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM move"));

        ExecuteSql("UPDATE session SET byte_offset = " + System.Text.Encoding.UTF8.GetByteCount(add + "\n"));
        Append(QueuedMove("10:00:00.001", 11, cls), CompletedMove("10:00:01.000", 11));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE tgt_kind = 'Equipped' AND caller LIKE '%AttachItem%'"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE result = 'succeed'"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Rotating_game_log_does_not_reuse_the_old_correlation_checkpoint(bool restart, bool clearSnapshotControl)
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var ingestor = new LogIngestor(_db, _dir);
        Append(AddMove("10:00:00.000", 11, cls), QueuedMove("10:00:00.001", 11, cls),
            CompletedMove("10:00:01.000", 11), SpawnMove("10:00:02.000", 11, cls, "783810961476"),
            "<2026-10-13T10:00:03.000Z> [Notice] <X> " + new string('x', 2000));
        ingestor.IngestAll();
        Assert.Equal("783810961476", Scalar("SELECT item_geid FROM move"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM ingest_correlation"));
        var oldCheckpoint = long.Parse(Scalar("SELECT byte_offset FROM ingest_correlation")!);

        var next = new[] { AddMove("11:00:00.000", 11, cls), QueuedMove("11:00:00.001", 11, cls),
            CompletedMove("11:00:01.000", 11), SpawnMove("11:00:02.000", 11, cls, "783810961477") };
        var spawnOffset = System.Text.Encoding.UTF8.GetByteCount(string.Join("\n", next.Take(3)) + "\n");
        Assert.True(spawnOffset < oldCheckpoint);
        File.WriteAllText(Path.Combine(_dir, "Game.log"), string.Join("\n", next) + "\n");
        if (clearSnapshotControl) ExecuteSql("DELETE FROM ingest_correlation");
        if (restart) ingestor = new LogIngestor(_db, _dir);

        ingestor.IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Contains("11:00:00", Scalar("SELECT first_ts FROM session"));
        Assert.Equal("succeed", Scalar("SELECT result FROM move"));
        Assert.Equal("783810961477", Scalar("SELECT COALESCE(item_geid, '<NULL>') FROM move"));
    }

    [Fact]
    public void Rotation_snapshot_invalidation_rolls_back_with_session_reset_and_retries()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var ingestor = new LogIngestor(_db, _dir);
        Append(AddMove("10:00:00.000", 11, cls), QueuedMove("10:00:00.001", 11, cls),
            CompletedMove("10:00:01.000", 11), SpawnMove("10:00:02.000", 11, cls, "783810961476"),
            "<2026-10-13T10:00:03.000Z> [Notice] <X> " + new string('x', 2000));
        ingestor.IngestAll();
        var oldOffset = Scalar("SELECT byte_offset FROM session");
        var oldSnapshot = Scalar("SELECT state_json FROM ingest_correlation");

        var next = new[] { AddMove("11:00:00.000", 11, cls), QueuedMove("11:00:00.001", 11, cls),
            CompletedMove("11:00:01.000", 11), SpawnMove("11:00:02.000", 11, cls, "783810961477") };
        File.WriteAllText(Path.Combine(_dir, "Game.log"), string.Join("\n", next) + "\n");
        ExecuteSql("CREATE TRIGGER l04_rotation_abort BEFORE UPDATE ON session WHEN NEW.byte_offset = 0 BEGIN SELECT RAISE(ABORT, 'l04 rotation retry'); END");

        Assert.Contains("l04 rotation retry", Assert.Throws<SqliteException>(() => ingestor.IngestAll()).Message);
        Assert.Equal(oldOffset, Scalar("SELECT byte_offset FROM session"));
        Assert.Equal(oldSnapshot, Scalar("SELECT state_json FROM ingest_correlation"));
        Assert.Equal("783810961476", Scalar("SELECT item_geid FROM move"));

        ExecuteSql("DROP TRIGGER l04_rotation_abort");
        ingestor.IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("783810961477", Scalar("SELECT COALESCE(item_geid, '<NULL>') FROM move"));
        Assert.Equal("succeed", Scalar("SELECT result FROM move"));
    }

    [Fact]
    public void Caller_survives_a_restart_between_add_move_and_queued()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:10:00.000", 13, cls));
        new LogIngestor(_db, _dir).IngestAll();

        Append(QueuedMove("10:10:00.001", 13, cls), CompletedMove("10:10:01.000", 13));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE tgt_kind = 'Equipped' AND caller LIKE '%AttachItem%'"));
    }

    [Fact]
    public void Completed_move_survives_a_restart_until_its_spawn_arrives()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:20:00.000", 17, cls), QueuedMove("10:20:00.001", 17, cls), CompletedMove("10:20:01.000", 17));
        new LogIngestor(_db, _dir).IngestAll();

        Append(SpawnMove("10:20:02.000", 17, cls, "783810961488"));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("783810961488", Scalar("SELECT item_geid FROM move"));
        Assert.Equal("succeed", Scalar("SELECT result FROM move"));
    }

    [Fact]
    public void Spawn_before_move_survives_a_restart_and_is_consumed_once()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(SpawnMove("10:25:00.000", 18, cls, "783810961488"));
        new LogIngestor(_db, _dir).IngestAll();

        Append(AddMove("10:25:01.000", 18, cls), QueuedMove("10:25:01.001", 18, cls),
            CompletedMove("10:25:02.000", 18));
        new LogIngestor(_db, _dir).IngestAll();
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("783810961488", Scalar("SELECT item_geid FROM move"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
    }

    [Fact]
    public void Spawn_between_move_and_completion_survives_two_restarts()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:26:00.000", 20, cls), QueuedMove("10:26:00.001", 20, cls));
        new LogIngestor(_db, _dir).IngestAll();

        Append(SpawnMove("10:26:01.000", 20, cls, "783810961489"));
        new LogIngestor(_db, _dir).IngestAll();

        Append(CompletedMove("10:26:02.000", 20));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("783810961489|succeed", Scalar("SELECT item_geid || '|' || result FROM move"));
    }

    [Fact]
    public void Every_append_boundary_matches_a_single_pass_and_repeated_passes_do_not_duplicate()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var lines = new[]
        {
            AddMove("10:27:00.000", 22, cls),
            QueuedMove("10:27:00.001", 22, cls),
            CompletedMove("10:27:01.000", 22),
            SpawnMove("10:27:02.000", 22, cls, "783810961490"),
            StoreLine("2026-10-13T10:27:03.000Z", 40),
        };

        var expected = RunIngest(lines, cuts: []);
        Assert.Contains("783810961490", expected);
        Assert.Contains("holdings=481104483777|", expected);
        Assert.Contains("rows=2", expected);
        foreach (var cut in Enumerable.Range(1, lines.Length - 1))
            Assert.Equal(expected, RunIngest(lines, [cut]));
        for (var first = 1; first < lines.Length - 1; first++)
            for (var second = first + 1; second < lines.Length; second++)
                Assert.Equal(expected, RunIngest(lines, [first, second]));
        Assert.Equal(expected, RunIngest(lines, [1, 2, 3]));
    }

    [Fact]
    public void Spawn_before_move_matches_single_pass_at_every_append_boundary()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var lines = new[]
        {
            SpawnMove("10:27:20.000", 34, cls, "783810961494"),
            AddMove("10:27:21.000", 34, cls),
            QueuedMove("10:27:21.001", 34, cls),
            CompletedMove("10:27:22.000", 34),
            StoreLine("2026-10-13T10:27:23.000Z", 41),
        };

        var expected = RunIngest(lines, cuts: []);
        Assert.Contains("783810961494", expected);
        Assert.Contains("holdings=481104483777|", expected);
        foreach (var cut in Enumerable.Range(1, lines.Length - 1))
            Assert.Equal(expected, RunIngest(lines, [cut]));
        Assert.Equal(expected, RunIngest(lines, [1, 2, 3, 4]));
    }

    [Fact]
    public void Rewinding_to_queued_after_completion_restores_correlation_and_enriches_once()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var add = AddMove("10:27:10.000", 32, cls);
        var queued = QueuedMove("10:27:10.001", 32, cls);
        var completed = CompletedMove("10:27:11.000", 32);
        Append(add, queued, completed);
        new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE result = 'succeed' AND item_geid IS NULL"));

        var queuedOffset = System.Text.Encoding.UTF8.GetByteCount(add + "\n");
        using (var cn = _db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "UPDATE session SET byte_offset = $offset";
            cmd.Parameters.AddWithValue("$offset", queuedOffset);
            cmd.ExecuteNonQuery();
        }

        Append(SpawnMove("10:27:12.000", 32, cls, "783810961493"));
        var stats = new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal(new FileInfo(LivePath).Length, long.Parse(Scalar("SELECT byte_offset FROM session")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
        var finalMove = Scalar("SELECT COALESCE(item_geid, '<NULL>') || '|' || COALESCE(result, '<NULL>') FROM move");
        Assert.True(finalMove == "783810961493|succeed", $"Recovered={stats.GeidsRecovered}; lines={stats.LinesRead}; move={finalMove}; rows={Scalar("SELECT line_offset || '|' || item_class || '|' || request_no FROM move")}");
        Assert.True(stats.ChangedHoldings);
        var holdings = HoldingResolver.Load(_db, asOf: new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)).ResolveAll();
        Assert.Contains(holdings, holding => holding.Geid == "783810961493");
    }

    [Fact]
    public void Batch_occurrence_keeps_each_class_and_consumes_only_its_matching_spawn()
    {
        const string firstClass = "cds_combat_superheavy_suit_01_04_01";
        const string secondClass = "grin_multitool_01_red01";
        Append(
            $"<2026-10-13T10:28:00.000Z> [Notice] <Add Inventory Management Move> New Request[24] Player[Pilot] Type[Interaction] " +
            "SourceInventory[204821708183:Location:2273540638] TargetInventory[INVALID] " +
            $"ItemClass[[{firstClass}] [{secondClass}]] StoredEntity[NULL] LocallyDetached[No] LocalAttached[NULL, ] PendingMoves[2, ] " +
            "Caller[CSCLocalPlayerPersonalThoughtComponent::AttachItem]",
            CompletedMove("10:28:01.000", 24),
            SpawnMove("10:27:59.000", 24, firstClass, "783810961491"),
            SpawnMove("10:27:58.000", 24, secondClass, "783810961492"));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("783810961491|783810961492", Scalar("SELECT group_concat(item_geid, '|') FROM (SELECT item_geid FROM move ORDER BY item_class)"));
    }

    [Fact]
    public void Interleaved_queued_lines_do_not_look_like_a_request_counter_reset()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:29:00.000", 25, cls), AddMove("10:29:00.001", 26, cls),
            QueuedMove("10:29:00.002", 25, cls), QueuedMove("10:29:00.003", 26, cls),
            CompletedMove("10:29:01.000", 25), CompletedMove("10:29:01.001", 26));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM move WHERE tgt_kind = 'Equipped' AND caller LIKE '%AttachItem%' AND result = 'succeed'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replaying_interleaved_adds_after_the_cursor_preserves_an_earlier_pending_caller(bool rewind)
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        const string geid = "783810961499";
        var add11 = AddMove("10:00:00.000", 11, cls);
        var neutral = "<2026-10-13T10:00:00.001Z> [Notice] <Unrelated> no inventory event";
        Append(add11, neutral,
            AddMove("10:00:01.000", 12, cls), QueuedMove("10:00:01.001", 12, cls), CompletedMove("10:00:02.000", 12),
            AddMove("10:00:03.000", 13, cls), QueuedMove("10:00:03.001", 13, cls), CompletedMove("10:00:04.000", 13));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM move"));
        using (var state = JsonDocument.Parse(Scalar("SELECT state_json FROM ingest_correlation")!))
        {
            var root = state.RootElement;
            Assert.Equal(0, root.GetProperty("Generation").GetInt64());
            Assert.Equal(13, root.GetProperty("LastRequestNo").GetInt32());
            Assert.Equal("CSCLocalPlayerPersonalThoughtComponent::AttachItem",
                root.GetProperty("PendingCallers").GetProperty("11").GetProperty("Caller").GetString());
        }

        if (rewind)
            ExecuteSql("UPDATE session SET byte_offset = " + System.Text.Encoding.UTF8.GetByteCount(add11 + "\n"));

        Append(QueuedMove("10:00:05.000", 11, cls), CompletedMove("10:00:06.000", 11), SpawnMove("10:00:07.000", 11, cls, geid));
        var stats = new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("3", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal(geid + "|succeed|Equipped|CSCLocalPlayerPersonalThoughtComponent::AttachItem",
            Scalar("SELECT COALESCE(item_geid, '<NULL>') || '|' || COALESCE(result, '<NULL>') || '|' || tgt_kind || '|' || caller FROM move WHERE request_no = 11"));
        Assert.True(stats.ChangedHoldings);
        var holdings = HoldingResolver.Load(_db, asOf: new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)).ResolveAll();
        Assert.Contains(holdings, holding => holding.Geid == geid);
    }

    [Fact]
    public void One_occurrence_accepts_disordered_timestamps_inside_the_pairing_windows()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:31:00.000", 27, cls), QueuedMove("10:30:59.000", 27, cls),
            CompletedMove("10:30:58.000", 27), SpawnMove("10:30:57.000", 27, cls, "783810961495"));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE request_no = 27 AND item_geid = '783810961495' AND result = 'succeed' AND caller LIKE '%AttachItem%'"));
    }

    [Fact]
    public void Persisted_correlation_maps_are_bounded_per_kind()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        var firstRequests = Enumerable.Range(1, 500).ToArray();
        var moves = firstRequests.Select(request => QueuedMove("11:00:00.000", request, cls));
        var firstAdds = firstRequests.Select(request => AddMove("11:00:00.000", request, cls));
        var callers = Enumerable.Range(501, 500).Select(request => AddMove("11:00:00.000", request, cls));
        var spawns = Enumerable.Range(0, 501).Select(i =>
            SpawnMove("11:00:00.000", 999, cls, (700_000_000_000L + i).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Append(firstAdds.Concat(moves).Concat(callers).Concat(spawns).ToArray());

        new LogIngestor(_db, _dir).IngestAll();

        using var state = JsonDocument.Parse(Scalar("SELECT state_json FROM ingest_correlation")!);
        var root = state.RootElement;
        var callerCount = root.GetProperty("PendingCallers").EnumerateObject().Count();
        var spawnCount = root.GetProperty("PendingGeids").EnumerateObject().Sum(x => x.Value.GetArrayLength());
        var issuedCount = root.GetProperty("IssuedRequests").GetArrayLength();
        var pendingMoveCount = root.GetProperty("MoveByRequest").EnumerateObject().Sum(x => x.Value.GetArrayLength());
        var requestHistoryCount = root.GetProperty("RequestHistory").GetArrayLength();
        Assert.Equal(500, callerCount);
        Assert.Equal(500, spawnCount);
        Assert.Equal(500, issuedCount);
        Assert.Equal(500, pendingMoveCount);
        Assert.Equal(500, requestHistoryCount);
    }

    private string RunIngest(string[] lines, int[] cuts)
    {
        var run = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(_dir, run);
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "Game.log");
        var db = new TrackerDb(Path.Combine(dir, "tracker.db"));
        db.Initialize();

        var boundary = cuts.Length == 0 ? lines.Length : cuts[0];
        File.WriteAllText(log, string.Join("\n", lines.Take(boundary)) + "\n");
        new LogIngestor(db, dir).IngestAll();

        foreach (var cut in cuts.Skip(1).Append(lines.Length))
        {
            File.AppendAllText(log, string.Join("\n", lines.Skip(boundary).Take(cut - boundary)) + "\n");
            boundary = cut;
            new LogIngestor(db, dir).IngestAll();
        }

        using var cn = db.Open();
        using var count = cn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM move";
        var rowCount = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT item_class,item_geid,src_kind,src_key,tgt_kind,tgt_key,amount,action,caller,result FROM move ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<NULL>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        reader.Close();

        var holdings = HoldingResolver.Load(db, asOf: new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero))
            .ResolveAll().OrderBy(holding => holding.Geid, StringComparer.Ordinal)
            .Select(holding => $"{holding.Geid}|{holding.ItemClass}|{holding.Quantity}|{string.Join(">", holding.Chain.Select(link => $"{link.Kind}:{link.Key}"))}");
        return $"rows={rowCount}\n" + string.Join("\n", rows) + "\nholdings=" + string.Join(";", holdings);
    }

    [Fact]
    public void An_old_spawn_more_than_the_pairing_window_before_a_move_is_rejected()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:30:00.000", 19, cls), QueuedMove("10:30:00.001", 19, cls),
            SpawnMove("10:28:59.000", 19, cls, "783810961489"));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE item_geid IS NULL"));
    }

    [Fact]
    public void A_completion_more_than_its_window_before_a_move_is_rejected()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:35:00.000", 21, cls), QueuedMove("10:35:00.001", 21, cls),
            CompletedMove("10:32:59.000", 21));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE result IS NULL"));
    }

    [Fact]
    public void A_disconnect_is_a_generation_boundary_for_later_spawns()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:40:00.000", 23, cls), QueuedMove("10:40:00.001", 23, cls),
            "<2026-10-13T10:40:01.000Z> [Notice] <Channel Disconnected> cause=30016 reason=\"Remote Disconnect\" frame=1",
            SpawnMove("10:40:02.000", 23, cls, "783810961490"));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE item_geid IS NULL"));
        Assert.Equal("lost", Scalar("SELECT result FROM move"));
    }

    [Fact]
    public void A_lower_request_number_starts_a_new_generation()
    {
        const string cls = "cds_combat_superheavy_suit_01_04_01";
        Append(AddMove("10:50:00.000", 25, cls), QueuedMove("10:50:00.001", 25, cls),
            AddMove("10:50:02.000", 1, cls), QueuedMove("10:50:02.001", 1, cls),
            SpawnMove("10:50:03.000", 25, cls, "783810961491"));

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move WHERE request_no = 25 AND item_geid IS NULL"));
    }

    private static string AddMove(string time, int request, string itemClass) =>
        $"<2026-10-13T{time}Z> [Notice] <Add Inventory Management Move> New Request[{request}] Player[Pilot] " +
        $"Type[Interaction] SourceInventory[204821708183:Location:2273540638] TargetInventory[INVALID] ItemClass[{itemClass}] " +
        "StoredEntity[NULL] LocallyDetached[No] LocalAttached[NULL, Body_ItemPort:Armor_Undersuit] PendingMoves[1, ] " +
        "Caller[CSCLocalPlayerPersonalThoughtComponent::AttachItem]";

    private static string QueuedMove(string time, int request, string itemClass) =>
        $"<2026-10-13T{time}Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[{request}] Type[Interaction] " +
        $"for 'Pilot' [204821708183] Source Inventory[204821708183:Location:2273540638] Target Inventory[INVALID]. " +
        $"Source[{itemClass}] amount[1] rank[a]. Target[NULL] amount[0] rank[]. Item[NONE] action[None]. RequestInProgress[0] CurrentProcess[]";

    private static string CompletedMove(string time, int request) =>
        $"<2026-10-13T{time}Z> [Notice] <Inventory Request Completed> Request[{request}] Player[Pilot] Result[succeed] Elapsed[0.001] PendingMoves[1]";

    private static string SpawnMove(string time, int request, string itemClass, string geid) =>
        $"<2026-10-13T{time}Z> [Notice] <UnstowPendingEntities> Unstow Request[{request}] for 'Pilot' [204821708183] " +
        $"finalized spawn of '{itemClass}_{geid}' [{geid}], 0 remaining";

    [Fact]
    public void Records_a_carry_into_the_hand_with_the_entity_its_spawn_names()
    {
        // A real sequence: drinking from a crate, lines as the game wrote them.
        Append(
            "<2026-08-04T14:58:39.486Z> [Notice] <Add Inventory Management Move> New request[49] Player[Pilot] " +
            "Type[Interaction] SourceInventory[746335892394:Container:0] TargetInventory[INVALID] " +
            "ItemClass[Drink_bottle_smoothie_02_a] StoredEntity[NULL] LocallyDetached[No] LocalAttached[NULL, ] " +
            "PendingMoves[1, ] Caller[CSCLocalPlayerPersonalThoughtComponent::DoInventoryGridInteractionCB]",
            "<2026-08-04T14:58:39.488Z> [Notice] <InventoryManagementRequest> Queued Request[49] Type[Interaction] " +
            "for 'Pilot' [204821708183] Source Inventory[746335892394:Container:0] Target Inventory[INVALID]. " +
            "Source[Drink_bottle_smoothie_02_a] amount[1] rank[amqwjuurwtehx]. Target[NULL] amount[0] rank[]. " +
            "Item[NONE] action[Carry]. RequestInProgress[0] CurrentProcess[]",
            "<2026-08-04T14:58:41.207Z> [Notice] <Player Inventory Request Complete> Request[49] for 'Pilot' " +
            "[204821708183] Type[Interaction] Result[Succeed] Item[750982306075] CanLockQueue[Yes].",
            "<2026-08-04T14:58:41.589Z> [Notice] <UnstowPendingEntities> Unstow Request[49] for 'Pilot' [204821708183] " +
            "finalized spawn of 'Drink_bottle_smoothie_02_a_750982306075' [750982306075], 4 remaining");

        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("Equipped", Scalar("SELECT tgt_kind FROM move"));
        Assert.Equal("750982306075", Scalar("SELECT item_geid FROM move"));
        Assert.Equal("Carry", Scalar("SELECT action FROM move"));
        Assert.Equal("Succeed", Scalar("SELECT result FROM move"));
    }

    /// <summary>Writes a rotated backup named for its build, as the game does.</summary>
    private void Backup(int build, string stamp, params string[] lines)
    {
        var dir = Path.Combine(_dir, "logbackups");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"Game Build({build}) {stamp}.log"), string.Join("\n", lines) + "\n");
    }

    /// <summary>
    /// A store at a named station, confirmed by the game: nothing about it costs score, so
    /// whatever the resolver takes off is down to what the test is about.
    /// </summary>
    private static string[] ConfirmedStoreAtLorville() =>
    [
        "<2026-08-04T09:59:00.000Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
        "Landing [0] -> [4005457614]. Location [0] -> [4005457614]. Pending [0]",
        "<2026-08-04T09:59:00.500Z> [Notice] <RequestLocationInventory> Player[Pilot] requested inventory for Location[Stanton1_Lorville]",
        StoreLine("2026-08-04T10:00:00.000Z", 1),
        "<2026-08-04T10:00:00.400Z> [Notice] <Inventory Request Completed> Request[1] Player[Pilot] Result[succeed] Elapsed[0.4] PendingMoves[1]",
    ];

    [Fact]
    public void A_placement_made_before_a_game_update_is_trusted_less_and_says_why()
    {
        // Updates move stored items server-side and log nothing, so a placement older than
        // the newest update is a lead, not a fact. Age alone costs nothing. Here the new
        // build never reaches a location, so there is nowhere to move the item to.
        Backup(12344265, "04 Aug 26 (10 17 03)", ConfirmedStoreAtLorville());
        Backup(12519617, "26 Aug 26 (20 30 32)", "<2026-08-27T00:30:37.588Z> [Notice] <X> first line after the update");
        new LogIngestor(_db, _dir).IngestAll();

        var resolver = HoldingResolver.Load(_db, asOf: new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
        var holding = Assert.Single(resolver.ResolveAll());

        Assert.Equal(0.5, holding.Score, precision: 3);
        Assert.Contains(holding.Caveats, c => c.Contains("game update to build 12519617"));
    }

    /// <summary>The first lines of a new build: the player spawns at Area18, and names it.</summary>
    private static string[] SpawnAtArea18() =>
    [
        "<2026-08-27T00:30:37.588Z> [Notice] <X> first line after the update",
        Arrive,
        "<2026-08-27T00:39:22.500Z> [Notice] <RequestLocationInventory> Player[Pilot] requested inventory for Location[Stanton3_Area18]",
    ];

    [Fact]
    public void A_game_update_moves_a_stored_item_to_where_the_player_spawned_after_it()
    {
        Backup(12344265, "04 Aug 26 (10 17 03)", ConfirmedStoreAtLorville());
        Backup(12519617, "26 Aug 26 (20 30 32)", SpawnAtArea18());
        new LogIngestor(_db, _dir).IngestAll();

        var resolver = HoldingResolver.Load(_db, asOf: new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
        var holding = Assert.Single(resolver.ResolveAll());

        Assert.Equal("2273540638", holding.Chain[^1].Key);
        Assert.Equal(0.5, holding.Score, precision: 3); // one update, not yet verified
        Assert.Contains(holding.Caveats, c => c.StartsWith("assumed moved here by the game update to build 12519617"));
    }

    [Fact]
    public void A_landing_zone_named_only_internally_is_shown_with_its_planet_and_system()
    {
        // Nothing but the internal string ever named Area18 here, and "Stanton3_Area18" is
        // not something the player would recognise; the orbit number is: ArcCorp.
        Append(SpawnAtArea18());
        new LogIngestor(_db, _dir).IngestAll();

        var place = PlaceCatalog.Load(_db).ByLocationId("2273540638");

        Assert.NotNull(place);
        Assert.Equal("Area18", place.Name);
        Assert.Equal(PlaceNameStatus.Pending, place.NameStatus); // one source: KnownPlaces
        Assert.Equal("ArcCorp", place.Planet);
        Assert.Equal("Area18 — ArcCorp (Stanton)", place.Label);
        Assert.Equal("Area18 — ArcCorp", place.LabelInSystem); // the picker already names the system
    }

    [Fact]
    public void A_sessions_spawn_is_its_first_location_even_across_passes()
    {
        // Only the first arrival is the spawn; a later stop in the same file must not replace
        // it, whether the next pass carries state over or starts cold.
        Append(Arrive);
        new LogIngestor(_db, _dir).IngestAll();

        Append(
            "<2026-08-27T01:10:00.000Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
            "Landing [2273540638] -> [4005457614]. Location [2273540638] -> [4005457614]. Pending [0]");
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("2273540638", Scalar("SELECT first_loc_id FROM session"));
        Assert.Equal("4005457614", Scalar("SELECT cur_loc_id FROM session"));
    }

    [Fact]
    public void An_update_the_player_resumed_from_where_they_logged_out_moves_nothing()
    {
        // 12572603 and 12660092 both spawned the player exactly where they had logged out.
        // Nothing says those updates moved anything, so the item stays put, only weighed.
        Backup(12344265, "04 Aug 26 (10 17 03)", ConfirmedStoreAtLorville());
        Backup(12519617, "26 Aug 26 (20 30 32)",
            "<2026-08-27T00:30:37.588Z> [Notice] <X> first line after the update",
            "<2026-08-27T00:39:21.956Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
            "Landing [0] -> [4005457614]. Location [0] -> [4005457614]. Pending [0]");
        new LogIngestor(_db, _dir).IngestAll();

        var resolver = HoldingResolver.Load(_db, asOf: new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
        var holding = Assert.Single(resolver.ResolveAll());

        Assert.Equal("4005457614", holding.Chain[^1].Key);
        Assert.False(resolver.MovedThePlayer(Assert.Single(resolver.Updates)));
        Assert.Contains(holding.Caveats, c => c.Contains("picked up where they had logged out"));
    }

    [Fact]
    public void An_update_that_respawns_the_player_under_a_reissued_id_moves_nothing()
    {
        // The game re-issues ids for the same station. Lorville under a new id is still where
        // the player logged out, which the internal inventory name shows.
        Backup(12344265, "04 Aug 26 (10 17 03)", ConfirmedStoreAtLorville());
        Backup(12519617, "26 Aug 26 (20 30 32)",
            "<2026-08-27T00:30:37.588Z> [Notice] <X> first line after the update",
            "<2026-08-27T00:39:21.956Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
            "Landing [0] -> [1234567890]. Location [0] -> [1234567890]. Pending [0]",
            "<2026-08-27T00:39:22.500Z> [Notice] <RequestLocationInventory> Player[Pilot] requested inventory for Location[Stanton1_Lorville]");
        new LogIngestor(_db, _dir).IngestAll();

        var resolver = HoldingResolver.Load(_db, asOf: new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
        var update = Assert.Single(resolver.Updates);
        var holding = Assert.Single(resolver.ResolveAll());

        Assert.Equal("1234567890", update.Spawn);
        Assert.Equal("4005457614", update.LeftFrom);
        Assert.False(resolver.MovedThePlayer(update));
        Assert.Equal("4005457614", holding.Chain[^1].Key);
        Assert.Contains(holding.Caveats, c => c.Contains("picked up where they had logged out"));
    }

    [Fact]
    public void An_update_with_no_logout_place_on_record_moves_nothing()
    {
        // The old build's logs never place the player: no <Update Inventory Location> at all.
        // Where they logged out is unknown, so the spawn is no evidence the update moved them.
        Backup(12344265, "04 Aug 26 (10 17 03)",
            "<2026-08-04T09:59:00.500Z> [Notice] <RequestLocationInventory> Player[Pilot] requested inventory for Location[Stanton1_Lorville]",
            StoreLine("2026-08-04T10:00:00.000Z", 1),
            "<2026-08-04T10:00:00.400Z> [Notice] <Inventory Request Completed> Request[1] Player[Pilot] Result[succeed] Elapsed[0.4] PendingMoves[1]");
        Backup(12519617, "26 Aug 26 (20 30 32)", SpawnAtArea18());
        new LogIngestor(_db, _dir).IngestAll();

        var resolver = HoldingResolver.Load(_db, asOf: new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
        var update = Assert.Single(resolver.Updates);
        var holding = Assert.Single(resolver.ResolveAll());

        Assert.Equal("2273540638", update.Spawn);
        Assert.Null(update.LeftFrom);
        Assert.False(resolver.MovedThePlayer(update));
        Assert.Equal("4005457614", holding.Chain[^1].Key);
        Assert.DoesNotContain(holding.Caveats, c => c.Contains("moved here by the game update"));
        Assert.Contains(holding.Caveats, c => c.Contains("do not say where the player logged out"));
    }

    /// <summary>A quantum jump to <paramref name="point"/>, arriving, then entering <paramref name="location"/>.</summary>
    private static string[] QuantumTo(string point, string location, int minute, double enterAfterSeconds = 2.5)
    {
        var at = new DateTimeOffset(2026, 9, 12, 13, minute, 0, TimeSpan.Zero);
        string Ts(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        return
        [
            $"<{Ts(at)}> [Notice] <Player Selected Quantum Target - Local> [ItemNavigation] | NOT AUTH | " +
            $"SHIP_1[1]|CSCItemNavigation::OnPlayerSelectedQuantumTarget|Player has selected point {point} as their destination, routing locally",
            $"<{Ts(at.AddSeconds(30))}> [Notice] <Quantum Drive Arrived - Arrived at Final Destination> [ItemNavigation] | " +
            "NOT AUTH | SHIP_1[1]|CSCItemNavigation::OnQuantumDriveArrived|Quantum Drive has arrived at final destination",
            $"<{Ts(at.AddSeconds(30 + enterAfterSeconds))}> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
            $"Landing [0] -> [0]. Location [1902223495] -> [{location}]. Pending [0]",
        ];
    }

    [Fact]
    public void A_spot_in_open_space_is_named_after_the_quantum_point_the_player_arrived_at()
    {
        // The player parked a mining ship by an asteroid field and logged out there. No
        // station inventory, no route plotted from it: the jump is the only name it gets.
        File.WriteAllText(LivePath, string.Join("\n", QuantumTo("ab_mine_stanton3_sml_003", "3108822724", minute: 9)) + "\n");
        new LogIngestor(_db, _dir).IngestAll();

        var place = PlaceCatalog.Load(_db).ByLocationId("3108822724");

        Assert.NotNull(place);
        Assert.Equal("near ab_mine_stanton3_sml_003", place.Name);
        Assert.Equal("Stanton", place.System);
        Assert.Equal("ab_mine_stanton3_sml_003", place.QuantumPoint);
    }

    [Fact]
    public void A_location_reached_on_the_way_to_several_points_is_not_named_after_any()
    {
        // A system's open space is entered on the way to everything in it.
        File.WriteAllText(LivePath, string.Join("\n",
            QuantumTo("rs_asmbl_keeger_02", "126935608", minute: 1)
                .Concat(QuantumTo("rs_ext_nyx-pyro_jp1", "126935608", minute: 5))) + "\n");
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Null(PlaceCatalog.Load(_db).ByLocationId("126935608"));
    }

    [Theory]
    [InlineData("MISSION_QT_Bounty_Beacon_732995237455", 2.5)] // a one-off beacon
    [InlineData("ab_mine_stanton3_sml_003", 45)]                 // entered long after arriving
    public void Only_a_lasting_point_entered_right_on_arrival_names_a_place(string point, double enterAfterSeconds)
    {
        File.WriteAllText(LivePath, string.Join("\n", QuantumTo(point, "3108822724", minute: 9, enterAfterSeconds)) + "\n");
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM place_evidence WHERE kind = 'arrival'"));
    }

    [Fact]
    public void An_old_placement_within_one_build_keeps_its_score()
    {
        Backup(12344265, "04 Aug 26 (10 17 03)", ConfirmedStoreAtLorville());
        new LogIngestor(_db, _dir).IngestAll();

        var resolver = HoldingResolver.Load(_db, asOf: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var holding = Assert.Single(resolver.ResolveAll());

        Assert.Equal(1.0, holding.Score, precision: 3);
        Assert.Contains(holding.Caveats, c => c.StartsWith("last seen"));
    }

    [Theory]
    [InlineData(0, 0, 0.5)]   // nothing checked: the unverified default
    [InlineData(4, 0, 0.5)]   // too few checks to go on
    [InlineData(13, 0, 0.2)]  // everything moved: floored, the old place is still the best lead
    [InlineData(10, 8, 0.8)]  // mostly survived
    [InlineData(6, 6, 1.0)]   // all survived: the update is harmless
    public void An_updates_weight_is_what_the_logs_showed_of_it(int checkedCount, int survived, double factor)
    {
        Assert.Equal(factor, new HoldingResolver.UpdateSurvival(checkedCount, survived).Factor, precision: 3);
    }

    [Fact]
    public void A_parser_upgrade_re_ingests_history_that_was_read_by_the_old_one()
    {
        // Rotated files are never reopened once complete, so without this a parser fix would
        // only ever reach lines written after the upgrade.
        File.WriteAllText(LivePath, StoreLine("2026-07-16T10:00:00.000Z", 1) + "\n");
        new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));

        using (var cn = _db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "UPDATE meta SET value = '0' WHERE key = 'parser_version'";
            cmd.ExecuteNonQuery();
        }

        _db.Initialize();

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM session"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM move"));

        // Cleared is only half of it: the next pass must read the history again from the top.
        new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
    }

    private static string Attached(string geid, string cls, string port) =>
        "<2026-09-25T15:35:47.776Z> [Notice] <AttachmentReceived> Player[Pilot] " +
        $"Attachment[{cls}_{geid}, {cls}, {geid}] Status[persistent] Port[{port}] Elapsed[62.1]";

    [Fact]
    public void A_burst_split_between_two_passes_is_one_burst_and_its_parts_find_their_parent()
    {
        // A whole burst lands within two milliseconds, but a watcher pass can still end in the
        // middle of it. A fresh ingestor has no carried state and must pick up from the store.
        Append(Attached("845736965844", "qrt_utility_heavy_helmet_01_01_03", "Armor_Helmet"));
        new LogIngestor(_db, _dir).IngestAll();

        Append(Attached("845736965845", "FP_Visor", "helmet_visor"));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("845736965844", Scalar("SELECT parent_geid FROM attachment_sighting WHERE geid = '845736965845'"));
        Assert.Equal("1", Scalar("SELECT COUNT(DISTINCT burst_id) FROM attachment_sighting"));
    }

    [Fact]
    public void A_completion_read_after_a_restart_overwrites_a_lost_mark()
    {
        // The disconnect voids the pending request; the tracker restarts; the completion
        // that still turns up must be matched to it.
        Append(
            StoreLine("2026-09-25T15:44:48.161Z", 28),
            "<2026-09-25T15:45:00.000Z> [Notice] <Channel Disconnected> cause=30016 reason=\"Remote Disconnect\"");
        new LogIngestor(_db, _dir).IngestAll();
        Assert.Equal("lost", Scalar("SELECT result FROM move"));

        Append("<2026-09-25T15:45:30.000Z> [Notice] <Inventory Request Completed> Request[28] Player[Pilot] Result[succeed] Elapsed[42.0]");
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("succeed", Scalar("SELECT result FROM move"));
    }

    [Fact]
    public void A_line_stamped_before_the_previous_one_starts_a_new_burst()
    {
        Append(
            Attached("845736965844", "qrt_utility_heavy_helmet_01_01_03", "Armor_Helmet"),
            Attached("845736965845", "FP_Visor", "helmet_visor").Replace("15:35:47.776", "15:30:00.000"));
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("2", Scalar("SELECT COUNT(DISTINCT burst_id) FROM attachment_sighting"));
        Assert.Equal("", Scalar("SELECT COALESCE(parent_geid, '') FROM attachment_sighting WHERE geid = '845736965845'"));
    }

    [Fact]
    public void A_local_attachment_is_not_recorded_and_a_placeholder_is_recorded_but_not_worn()
    {
        Append(
            "<2026-09-25T15:35:06.912Z> [Notice] <AttachmentReceived> Player[Pilot] " +
            "Attachment[klwe_pistol_energy_01_200000000222, klwe_pistol_energy_01, 200000000222] " +
            "Status[persistent] Port[wep_sidearm] Elapsed[20.0]",
            "<2026-09-25T15:57:37.130Z> [Notice] <AttachmentReceived> Player[Pilot] " +
            "Attachment[Inventory_LocalAttach_Item, Default, 10721] Status[local] Port[Armor_Helmet] Elapsed[0.0025]");
        new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM attachment_sighting WHERE geid = '10721'"));
        Assert.Equal("1", Scalar("SELECT placeholder FROM attachment_sighting WHERE geid = '200000000222'"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM attachment"));
    }

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
