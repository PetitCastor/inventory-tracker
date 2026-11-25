using System.Globalization;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Model;
using InventoryTracker.Resolve;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

/// <summary>
/// A move whose class list names a pile once ("ItemClass[[a] [b] ]") says nothing of how many
/// units that pile holds. Dragging a pile of 5 moved all 5; the replay used to move one. Every
/// line is built here with a made-up player and made-up ids, in the shapes the game writes.
/// </summary>
public sealed class ListedMoveTests : IDisposable
{
    private const string Can = "Drink_can_fizzz_01_muscat_a";
    private const string Bar = "food_bar_test_01";

    private const string StationKey = "888812681";
    private const string BackpackKey = "944861140196";
    private const string StationRef = "204821708183:Location:" + StationKey;
    private const string BackpackRef = BackpackKey + ":Container:0";
    private const string Elsewhere = "947542443304:Container:0";

    private static readonly Holding Station = new(InventoryKind.Location, StationKey);
    private static readonly Holding Backpack = new(InventoryKind.Container, BackpackKey);
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-11-08T10:00:00Z");
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-11-08T20:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public ListedMoveTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        _db.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static string T(int seconds) =>
        Origin.AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Completed(int request, int seconds, string result = "succeed") =>
        $"<{T(seconds + 1)}> [Notice] <Inventory Request Completed> Request[{request}] Player[Pilot] " +
        $"Result[{result}] Elapsed[0.5] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]";

    /// <summary>A class-level move of <paramref name="amount"/> units, as the Queued line counts them.</summary>
    private static string[] ClassMove(
        int request, int seconds, string itemClass, int amount, string source, string target,
        string caller = "CSCLocalPlayerPersonalThoughtComponent::OnInventoryMoveItemOntoUnoccupiedPosition") =>
    [
        $"<{T(seconds)}> [Notice] <Add Inventory Management Move> New Request[{request}] Player[Pilot] Type[Move] " +
        $"SourceInventory[{source}] TargetInventory[{target}] ItemClass[{itemClass}] StoredEntity[NULL] " +
        $"LocallyDetached[No] LocalAttached[NULL, ] PendingMoves[1, ] Caller[{caller}] [Team_CoreGameplayFeatures][Inventory]",
        $"<{T(seconds)}> [Notice] <Inventory Mgmt Request Queued> Queued Request[{request}] Type[Move] for 'Pilot' [204821708183] " +
        $"Source Inventory[{source}] Target Inventory[{target}]. Source[{itemClass}] amount[{amount}] rank[]. " +
        "Target[NULL] amount[0] rank[x]. Item[NONE] action[None]. RequestInProgress[1] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]",
        Completed(request, seconds),
    ];

    /// <summary>An entity the log names, put into <paramref name="target"/>.</summary>
    private static string[] Named(int request, int seconds, string itemClass, string geid, string target) =>
    [
        $"<{T(seconds)}> [Notice] <Inventory Mgmt Request Queued> Queued Request[{request}] Type[Store] for 'Pilot' [204821708183] " +
        $"Source Inventory[INVALID] Target Inventory[{target}]. Source[NULL] amount[0] rank[]. Target[NULL] amount[0] " +
        $"rank[]. Item[{itemClass}_{geid}] action[None]. RequestInProgress[0] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]",
        Completed(request, seconds),
    ];

    /// <summary>The line the game writes for a multi-select drag; it is the batch's only record.</summary>
    private static string BatchLine(
        int request, int seconds, string source, string target, IEnumerable<string> classes,
        string caller = "CSCLocalPlayerPersonalThoughtComponent::HandleComponentEvent") =>
        $"<{T(seconds)}> [Notice] <Add Inventory Management Move> New Request[{request}] Player[Pilot] Type[Move] " +
        $"SourceInventory[{source}] TargetInventory[{target}] " +
        $"ItemClass[{string.Join(" ", classes.Select(c => $"[{c}]"))} ] StoredEntity[NULL] LocallyDetached[No] " +
        $"LocalAttached[NULL, ] PendingMoves[1, 1 2] Caller[{caller}] [Team_CoreGameplayFeatures][Inventory]";

    private static string[] Batch(
        int request, int seconds, string source, string target, string[] classes, string result = "succeed") =>
        [BatchLine(request, seconds, source, target, classes), Completed(request, seconds, result)];

    private HoldingResolver Resolve(params string[][] groups)
    {
        File.WriteAllText(Path.Combine(_dir, "Game.log"), string.Join("\n", groups.SelectMany(g => g)) + "\n");
        new LogIngestor(_db, _dir).IngestAll();
        return HoldingResolver.Load(_db, AsOf);
    }

    private static int Count(HoldingResolver resolver, string itemClass, Holding holder) =>
        resolver.ResolveAll().Where(h => h.ItemClass == itemClass && h.Holder == holder).Sum(h => h.Quantity);

    [Fact]
    public void Three_named_cans_in_a_bag_all_go_with_one_entry_and_none_becomes_ambiguous()
    {
        var resolver = Resolve(
            Named(1, 0, Can, "111111111111", BackpackRef),
            Named(2, 5, Can, "222222222222", BackpackRef),
            Named(3, 10, Can, "333333333333", BackpackRef),
            Batch(4, 20, BackpackRef, StationRef, [Can]));

        foreach (var geid in new[] { "111111111111", "222222222222", "333333333333" })
        {
            var can = resolver.Resolve(geid);
            Assert.NotNull(can);
            Assert.Equal(Station, can.Holder);
            Assert.False(can.IdentityAmbiguous);
        }

        Assert.Equal(0, Count(resolver, Can, Backpack));
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 4)]
    public void Named_cans_listed_in_several_entries_all_arrive_and_none_is_ambiguous(int held, int entries)
    {
        var geids = Enumerable.Range(1, held).Select(i => $"{i}{i}{i}{i}{i}{i}{i}{i}{i}{i}{i}{i}").ToList();
        var stores = geids.Select((g, i) => Named(i + 1, i * 5, Can, g, BackpackRef)).ToArray();

        var resolver = Resolve([.. stores, Batch(held + 1, 100, BackpackRef, StationRef, [.. Enumerable.Repeat(Can, entries)])]);

        foreach (var geid in geids)
        {
            var can = resolver.Resolve(geid);
            Assert.NotNull(can);
            Assert.Equal(Station, can.Holder);
            Assert.False(can.IdentityAmbiguous);
        }
    }

    [Fact]
    public void An_ordinary_move_of_one_among_three_named_cans_flags_the_one_it_takes()
    {
        var resolver = Resolve(
            Named(1, 0, Can, "111111111111", BackpackRef),
            Named(2, 5, Can, "222222222222", BackpackRef),
            Named(3, 10, Can, "333333333333", BackpackRef),
            ClassMove(4, 20, Can, 1, BackpackRef, StationRef));

        var moved = Assert.Single(resolver.ResolveAll(), h => h.Geid is not null && h.Holder == Station);
        Assert.True(moved.IdentityAmbiguous);
    }

    [Fact]
    public void A_pile_of_five_listed_once_moves_whole()
    {
        var resolver = Resolve(
            ClassMove(1, 0, Can, 5, Elsewhere, BackpackRef),
            Batch(2, 20, BackpackRef, StationRef, [Can]));

        Assert.Equal(5, Count(resolver, Can, Station));
        Assert.Equal(0, Count(resolver, Can, Backpack));
    }

    [Fact]
    public void A_class_listed_twice_over_three_held_moves_all_three()
    {
        var resolver = Resolve(
            ClassMove(1, 0, Can, 3, Elsewhere, BackpackRef),
            Batch(2, 20, BackpackRef, StationRef, [Can, Can]));

        Assert.Equal(3, Count(resolver, Can, Station));
        Assert.Equal(0, Count(resolver, Can, Backpack));
    }

    [Fact]
    public void Nothing_known_of_the_source_moves_one_unit_per_entry_as_before()
    {
        var resolver = Resolve(Batch(1, 20, BackpackRef, StationRef, [Can, Can]));

        Assert.Equal(2, Count(resolver, Can, Station));
    }

    [Fact]
    public void Classes_the_list_does_not_name_stay_where_they_were()
    {
        var resolver = Resolve(
            ClassMove(1, 0, Can, 1, Elsewhere, BackpackRef),
            ClassMove(2, 5, Bar, 1, Elsewhere, BackpackRef),
            ClassMove(3, 10, "other_class_one", 1, Elsewhere, BackpackRef),
            ClassMove(4, 15, "other_class_two", 1, Elsewhere, BackpackRef),
            Batch(5, 30, BackpackRef, StationRef, [Can, Bar]));

        Assert.Equal(1, Count(resolver, Can, Station));
        Assert.Equal(1, Count(resolver, Bar, Station));
        Assert.Equal(1, Count(resolver, "other_class_one", Backpack));
        Assert.Equal(1, Count(resolver, "other_class_two", Backpack));
    }

    [Fact]
    public void A_failed_request_and_a_lost_one_move_nothing()
    {
        var resolver = Resolve(
            ClassMove(1, 0, Can, 3, Elsewhere, BackpackRef),
            Batch(2, 20, BackpackRef, StationRef, [Can], result: "failed"),
            [BatchLine(3, 30, BackpackRef, StationRef, [Can])],
            [$"<{T(40)}> [Notice] <Channel Disconnected> cause=30016 reason=\"Remote Disconnect\" frame=1"]);

        Assert.Equal(3, Count(resolver, Can, Backpack));
        Assert.Equal(0, Count(resolver, Can, Station));
    }

    [Fact]
    public void A_plain_single_move_still_moves_what_it_says()
    {
        var resolver = Resolve(
            ClassMove(1, 0, Can, 5, Elsewhere, BackpackRef),
            ClassMove(2, 20, Can, 1, BackpackRef, StationRef));

        Assert.Equal(1, Count(resolver, Can, Station));
        Assert.Equal(4, Count(resolver, Can, Backpack));
    }

    [Fact]
    public void A_list_the_game_cut_moves_the_classes_it_read_whole_and_leaves_the_rest()
    {
        const string first = "test_item_class_01";
        const string last = "test_item_class_60";
        var listed = new[] { first, first }.Concat(GameLogCut.Classes(60).Skip(1)).ToList();
        var full = GameLogCut.FullBatchLine(2, BackpackRef, listed).Replace("2026-11-08T16:13:09.012Z", T(20));
        var cut = GameLogCut.Apply(full);
        Assert.DoesNotContain(last, cut, StringComparison.Ordinal);

        var resolver = Resolve(
            ClassMove(1, 0, first, 3, Elsewhere, StationRef),
            ClassMove(3, 5, last, 3, Elsewhere, StationRef),
            [cut, Completed(2, 20)]);

        Assert.Equal(3, Count(resolver, first, Backpack));
        Assert.Equal(0, Count(resolver, first, Station));
        Assert.Equal(3, Count(resolver, last, Station));
    }

    [Fact]
    public void A_named_box_holding_the_target_stays_where_it_is()
    {
        const string box = "qrt_box_test_01";
        var resolver = Resolve(
            Named(1, 0, box, "555555555555", StationRef),
            Named(2, 5, box, "444444444444", StationRef),
            Batch(3, 20, StationRef, "444444444444:Container:0", [box]));

        Assert.Equal(Station, resolver.Resolve("444444444444")!.Holder);
        Assert.Equal(new Holding(InventoryKind.Container, "444444444444"), resolver.Resolve("555555555555")!.Holder);
    }

    [Fact]
    public void A_list_onto_the_body_keeps_to_one_unit()
    {
        var resolver = Resolve(
            ClassMove(1, 0, Can, 3, Elsewhere, BackpackRef),
            [BatchLine(2, 20, BackpackRef, "INVALID", [Can], caller: "CSCLocalPlayerPersonalThoughtComponent::AttachItem")],
            [Completed(2, 20)]);

        Assert.Equal(1, Count(resolver, Can, new Holding(InventoryKind.Equipped, "")));
        Assert.Equal(2, Count(resolver, Can, Backpack));
    }

    [Fact]
    public void Reading_the_same_file_again_adds_nothing()
    {
        var resolver = Resolve(
            ClassMove(1, 0, Can, 5, Elsewhere, BackpackRef),
            Batch(2, 20, BackpackRef, StationRef, [Can]));
        Assert.Equal(5, Count(resolver, Can, Station));

        var rows = Scalar("SELECT COUNT(*) FROM move");

        // Rewinding the stored offset makes the second pass read every line again.
        using (var cn = _db.Open())
        {
            using var rewind = cn.CreateCommand();
            rewind.CommandText = "UPDATE session SET byte_offset = 0";
            rewind.ExecuteNonQuery();
        }

        new LogIngestor(_db, _dir).IngestAll();
        var again = HoldingResolver.Load(_db, AsOf);

        Assert.Equal(rows, Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal(5, Count(again, Can, Station));
        Assert.Equal(0, Count(again, Can, Backpack));
    }

    [Fact]
    public void A_listed_move_into_the_same_station_by_another_raw_loses_nothing()
    {
        var resolver = Resolve(
            ClassMove(1, 0, Can, 3, Elsewhere, StationRef),
            Batch(2, 20, StationRef, "999999999999:Location:" + StationKey, [Can]));

        Assert.Equal(3, Count(resolver, Can, Station));
        // The 3 inferred units are the setup's (it moves from a source the log never filled);
        // the listed move itself infers none.
        Assert.Equal(3, resolver.ReplayStats.UnitsInferred);
        // 3 for the setup and 1 for the list: taken as a pile, the list would count 3.
        Assert.Equal(4, resolver.ReplayStats.ClassUnits);
    }

    private string Scalar(string sql)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }
}
