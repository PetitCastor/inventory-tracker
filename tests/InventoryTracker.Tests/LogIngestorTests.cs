using InventoryTracker.Data;
using InventoryTracker.Ingest;
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

    private const string Arrive =
        "<2026-08-27T00:39:21.956Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
        "Landing [0] -> [2273540638]. Location [0] -> [2273540638]. Pending [0]";

    private const string OpenSuit =
        "<2026-08-27T00:42:01.289Z> [Notice] <Inventory Token Flow> Requesting access token for " +
        "User[Pilot, 204821708183] on Inventory[cds_combat_superheavy_suit_01_04_01_783810961476, 783810961476]";

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
