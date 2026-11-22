using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Model;
using InventoryTracker.Resolve;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

/// <summary>
/// The move-all button: one request whose line names no item. Lines are from 2026-11-22 at QV
/// Logistics Station, with the player's name replaced: four Fizzz cans looted into the backpack
/// at 13:36, the backpack emptied into the station at 14:15, and the station emptied into the
/// backpack and back again at 18:32.
/// </summary>
public sealed class MoveAllTests : IDisposable
{
    private const string Can = "Drink_can_fizzz_01_muscat_a";
    private const string Station = "888812681";
    private const string Backpack = "944861140196";

    private static readonly string[] Looted =
    [
        "<2026-11-22T13:36:16.197Z> [Notice] <Add Inventory Management Move> New Request[130] Player[Pilot] Type[Move] " +
        "SourceInventory[947542443304:Container:0] TargetInventory[944861140196:Container:0] ItemClass[Drink_can_fizzz_01_muscat_a] " +
        "StoredEntity[NULL] LocallyDetached[No] LocalAttached[NULL, ] PendingMoves[2,  129] " +
        "Caller[CSCLocalPlayerPersonalThoughtComponent::OnInventoryMoveItemOntoUnoccupiedPosition] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T13:36:16.198Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[130] Type[Move] for 'Pilot' [204821708183] " +
        "Source Inventory[947542443304:Container:0] Target Inventory[944861140196:Container:0]. Source[Drink_can_fizzz_01_muscat_a] " +
        "amount[4] rank[]. Target[NULL] amount[0] rank[amsrimzrwymsr]. Item[NONE] action[None]. RequestInProgress[1] CurrentProcess[] " +
        "[Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T13:36:17.150Z> [Notice] <Inventory Request Completed> Request[130] Player[Pilot] Result[succeed] " +
        "Elapsed[0.953678] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]",
    ];

    private const string StoreAll =
        "<2026-11-22T14:15:36.528Z> [Notice] <Add Inventory Management Move> New Request[347] Player[Pilot] Type[Move] " +
        "SourceInventory[944861140196:Container:0] TargetInventory[204821708183:Location:888812681] ItemClass[] StoredEntity[NULL] " +
        "LocallyDetached[No] LocalAttached[NULL, ] PendingMoves[1, ] Caller[CSCLocalPlayerPersonalThoughtComponent::HandleComponentEvent] " +
        "[Team_CoreGameplayFeatures][Inventory]";

    private static readonly string[] StoredAll =
    [
        StoreAll,
        "<2026-11-22T14:15:36.531Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[347] Type[Move] for 'Pilot' [204821708183] " +
        "Source Inventory[944861140196:Container:0] Target Inventory[204821708183:Location:888812681]. Source[NULL] amount[0] rank[]. " +
        "Target[NULL] amount[0] rank[]. Item[NONE] action[None]. RequestInProgress[0] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T14:15:38.800Z> [Notice] <Inventory Request Completed> Request[347] Player[Pilot] Result[succeed] " +
        "Elapsed[2.271397] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]",
    ];

    private static readonly string[] RoundTrip =
    [
        "<2026-11-22T18:32:48.038Z> [Notice] <Add Inventory Management Move> New Request[78] Player[Pilot] Type[Move] " +
        "SourceInventory[204821708183:Location:888812681] TargetInventory[944861140196:Container:0] ItemClass[] StoredEntity[NULL] " +
        "LocallyDetached[No] LocalAttached[NULL, ] PendingMoves[1, ] Caller[CSCLocalPlayerPersonalThoughtComponent::HandleComponentEvent] " +
        "[Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T18:32:48.040Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[78] Type[Move] for 'Pilot' [204821708183] " +
        "Source Inventory[204821708183:Location:888812681] Target Inventory[944861140196:Container:0]. Source[NULL] amount[0] rank[]. " +
        "Target[NULL] amount[0] rank[]. Item[NONE] action[None]. RequestInProgress[0] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T18:32:49.406Z> [Notice] <Inventory Request Completed> Request[78] Player[Pilot] Result[succeed] " +
        "Elapsed[1.367789] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T18:32:50.527Z> [Notice] <Add Inventory Management Move> New Request[79] Player[Pilot] Type[Move] " +
        "SourceInventory[944861140196:Container:0] TargetInventory[204821708183:Location:888812681] ItemClass[] StoredEntity[NULL] " +
        "LocallyDetached[No] LocalAttached[NULL, ] PendingMoves[1, ] Caller[CSCLocalPlayerPersonalThoughtComponent::HandleComponentEvent] " +
        "[Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T18:32:50.529Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[79] Type[Move] for 'Pilot' [204821708183] " +
        "Source Inventory[944861140196:Container:0] Target Inventory[204821708183:Location:888812681]. Source[NULL] amount[0] rank[]. " +
        "Target[NULL] amount[0] rank[]. Item[NONE] action[None]. RequestInProgress[0] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T18:32:52.441Z> [Notice] <Inventory Request Completed> Request[79] Player[Pilot] Result[succeed] " +
        "Elapsed[1.914016] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]",
    ];

    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-11-22T19:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public MoveAllTests()
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

    private static InventoryEvent? Event(string raw)
    {
        Assert.True(LogLine.TryParse(raw, out var line));
        return InventoryEventParser.Parse(line);
    }

    private HoldingResolver Resolve(params string[] lines)
    {
        File.WriteAllText(Path.Combine(_dir, "Game.log"), string.Join("\n", lines) + "\n");
        new LogIngestor(_db, _dir).IngestAll();
        return HoldingResolver.Load(_db, AsOf);
    }

    [Fact]
    public void Reads_a_move_all()
    {
        var ev = Assert.IsType<MoveAllRequested>(Event(StoreAll));

        Assert.Equal(347, ev.RequestNo);
        Assert.Equal(InventoryKind.Container, ev.Source.Kind);
        Assert.Equal(InventoryKind.Location, ev.Target.Kind);
    }

    [Fact]
    public void A_move_all_carries_what_the_source_was_known_to_hold()
    {
        var resolver = Resolve([.. Looted, .. StoredAll]);

        var cans = Assert.Single(resolver.ResolveAll(), h => h.ItemClass == Can);
        Assert.Equal(4, cans.Quantity);
        Assert.Equal(new Holding(InventoryKind.Location, Station), cans.Holder);
        Assert.Contains(cans.Caveats, c => c.StartsWith("moved with a move-all", StringComparison.Ordinal));

        Assert.Equal(1, resolver.ReplayStats.MoveAlls);
        Assert.Equal(4, resolver.ReplayStats.UnitsMovedAll);
    }

    [Fact]
    public void Taking_everything_and_putting_it_back_leaves_it_where_it_was()
    {
        var resolver = Resolve([.. Looted, .. StoredAll, .. RoundTrip]);

        var cans = Assert.Single(resolver.ResolveAll(), h => h.ItemClass == Can);
        Assert.Equal(4, cans.Quantity);
        Assert.Equal(new Holding(InventoryKind.Location, Station), cans.Holder);
        Assert.Equal(3, resolver.ReplayStats.MoveAlls);
        Assert.DoesNotContain(resolver.ResolveAll(), h => h.Holder == new Holding(InventoryKind.Container, Backpack));
    }

    [Fact]
    public void A_move_all_out_of_somewhere_the_tracker_knew_nothing_of_places_nothing()
    {
        var resolver = Resolve(StoredAll);

        Assert.Empty(resolver.ResolveAll());
        Assert.Equal(1, resolver.ReplayStats.MoveAlls);
        Assert.Equal(0, resolver.ReplayStats.UnitsMovedAll);
    }

    [Fact]
    public void A_named_entity_follows_a_move_all()
    {
        // The can drunk at 14:16 was named on its way to the hand; here it is stored back into
        // the backpack by name first, so the backpack holds one entity and three counted cans.
        string[] named =
        [
            "<2026-11-22T13:40:00.000Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[140] Type[Store] for 'Pilot' [204821708183] " +
            "Source Inventory[INVALID] Target Inventory[944861140196:Container:0]. Source[NULL] amount[0] rank[]. Target[NULL] amount[0] " +
            "rank[]. Item[Drink_can_fizzz_01_muscat_a_949098877355] action[None]. RequestInProgress[0] CurrentProcess[] " +
            "[Team_CoreGameplayFeatures][Inventory]",
            "<2026-11-22T13:40:00.500Z> [Notice] <Inventory Request Completed> Request[140] Player[Pilot] Result[succeed] " +
            "Elapsed[0.5] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]",
        ];

        var resolver = Resolve([.. Looted, .. named, .. StoredAll]);

        var can = resolver.Resolve("949098877355");
        Assert.NotNull(can);
        Assert.Equal(new Holding(InventoryKind.Location, Station), can.Holder);
        Assert.Equal(4, resolver.ResolveAll().Where(h => h.ItemClass == Can).Sum(h => h.Quantity));
        Assert.DoesNotContain(resolver.ResolveAll(), h => h.Holder == new Holding(InventoryKind.Container, Backpack));
    }
}
