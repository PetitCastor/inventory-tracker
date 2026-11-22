using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Model;
using InventoryTracker.Resolve;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

/// <summary>
/// A purchase at a shop kiosk: the item is placed in the station the player stands in, and stays
/// tagged as bought wherever it goes next. Lines are from the purchase of 31 rifle magazines on
/// 2026-11-22 at 12:50, and the moves that took them out of the station six minutes later,
/// with the player's name replaced.
/// </summary>
public sealed class ShopPurchaseTests : IDisposable
{
    private const string Mag = "behr_rifle_ballistic_03_mag";
    private const string Station = "141810852";
    private const string Box = "944861140196";
    private const string Shop = "SCShop_LiveFire_Truckstop02-001";

    private const string Arrived =
        "<2026-11-22T12:49:14.728Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
        "Landing [141810852] -> [141810852]. Location [3310153053] -> [141810852]. Pending [0] [Team_CoreGameplayFeatures][Inventory]";

    private const string Bought =
        "<2026-11-22T12:50:25.311Z> [Notice] <CEntityComponentShopUIProvider::SendShopBuyRequest> Sending SShopBuyRequest - " +
        "playerId[204821708183] shopId[926576523088] shopName[SCShop_LiveFire_Truckstop02-001] kioskId[926576523087] " +
        "client_price[9083.000000] itemClassGUID[dbd68e25-d363-46ad-a924-be7337fd8305] itemName[behr_rifle_ballistic_03_mag] " +
        "quantity[31]  [Team_CoreGameplayFeatures][Shops][UI]";

    private const string Answered =
        "<2026-11-22T12:50:25.623Z> [Notice] <CEntityComponentShopUIProvider::RmShopFlowResponse> Received ShopFlowResponse - " +
        "playerId[204821708183] shopId[926576523088] shopName[SCShop_LiveFire_Truckstop02-001] kioskId[926576523087] " +
        "kioskState[BuyRequestProcessing] result[Success] type[Buying] [Team_CoreGameplayFeatures][Shops][UI]";

    private static readonly string[] TakenOut =
    [
        "<2026-11-22T12:56:37.478Z> [Notice] <Add Inventory Management Move> New Request[35] Player[Pilot] Type[Move] " +
        "SourceInventory[204821708183:Location:141810852] TargetInventory[944861140196:Container:0] ItemClass[behr_rifle_ballistic_03_mag] " +
        "StoredEntity[NULL] LocallyDetached[No] LocalAttached[NULL, ] PendingMoves[1, ] " +
        "Caller[CSCLocalPlayerPersonalThoughtComponent::OnInventoryMoveItemOntoUnoccupiedPosition] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T12:56:37.480Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[35] Type[Move] for 'Pilot' [204821708183] " +
        "Source Inventory[204821708183:Location:141810852] Target Inventory[944861140196:Container:0]. Source[behr_rifle_ballistic_03_mag] " +
        "amount[31] rank[amsriefazcqjn]. Target[NULL] amount[0] rank[amsrifklxvndn]. Item[NONE] action[None]. " +
        "RequestInProgress[0] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T12:56:37.898Z> [Notice] <Inventory Request Completed> Request[35] Player[Pilot] Result[succeed] " +
        "Elapsed[0.419046] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T12:56:39.895Z> [Notice] <Add Inventory Management Move> New Request[36] Player[Pilot] Type[Interaction] " +
        "SourceInventory[944861140196:Container:0] TargetInventory[INVALID] ItemClass[behr_rifle_ballistic_03_mag] StoredEntity[NULL] " +
        "LocallyDetached[No] LocalAttached[NULL, Body_ItemPort:Armor_Undersuit:Armor_Torso:magazine_attach_6] PendingMoves[1, ] " +
        "Caller[CSCLocalPlayerPersonalThoughtComponent::AttachItem] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T12:56:39.898Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[36] Type[Interaction] for 'Pilot' [204821708183] " +
        "Source Inventory[944861140196:Container:0] Target Inventory[INVALID]. Source[behr_rifle_ballistic_03_mag] amount[1] " +
        "rank[amsrifklxvndn]. Target[NULL] amount[0] rank[]. Item[NONE] action[None]. RequestInProgress[0] CurrentProcess[] " +
        "[Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T12:56:40.455Z> [Notice] <Inventory Request Completed> Request[36] Player[Pilot] Result[succeed] " +
        "Elapsed[0.560376] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-22T12:56:40.543Z> [Notice] <UnstowPendingEntities> Unstow Request[36] for 'Pilot' [204821708183] finalized spawn of " +
        "'behr_rifle_ballistic_03_mag_945901578009' [945901578009], 0 remaining [Team_CoreGameplayFeatures][Inventory]",
    ];

    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-11-22T13:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public ShopPurchaseTests()
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

    private IngestStats Ingest(params string[] lines)
    {
        File.WriteAllText(Path.Combine(_dir, "Game.log"), string.Join("\n", lines) + "\n");
        return new LogIngestor(_db, _dir).IngestAll();
    }

    [Fact]
    public void Reads_a_kiosk_purchase()
    {
        var ev = Assert.IsType<ShopItemBought>(Event(Bought));

        Assert.Equal("204821708183", ev.PlayerId);
        Assert.Equal("926576523088", ev.ShopId);
        Assert.Equal(Shop, ev.ShopName);
        Assert.Equal(Mag, ev.ItemClass);
        Assert.Equal(31, ev.Quantity);
        Assert.Equal(9083m, ev.Price);
    }

    [Fact]
    public void Reads_the_kiosk_answer()
    {
        var ev = Assert.IsType<ShopBuyAnswered>(Event(Answered));

        Assert.Equal("926576523088", ev.ShopId);
        Assert.True(ev.Succeeded);
    }

    [Fact]
    public void A_bar_purchase_is_not_read()
    {
        // From 2026-11-21 at 20:00: a drink bought at a bar. It never shows up in an inventory.
        const string bar =
            "<2026-11-21T20:00:15.270Z> [Notice] <CEntityComponentShoppingProvider::SendStandardItemBuyRequest> Sending SShopBuyRequest - " +
            "playerId[204821708183] shopId[926495079559] shopName[SCShop_Entity_CubbyBlast_Area18] kioskId[0] client_price[7.000000] " +
            "itemClassGUID[72b91153-5a3e-4d71-af5c-f6c57ea2891a] itemName[Drink_bottle_cruz_01_lux_a] quantity[1] currencyType[UEC] " +
            "[Team_CoreGameplayFeatures][Shops][UI]";

        Assert.Null(Event(bar));
    }

    [Fact]
    public void A_purchase_lands_in_the_station_the_player_stands_in_tagged_as_bought()
    {
        var stats = Ingest(Arrived, Bought, Answered);

        Assert.Equal(1, stats.PurchasesRecorded);
        Assert.Equal(1, stats.PurchasesAnswered);
        Assert.True(stats.ChangedHoldings);

        var resolver = HoldingResolver.Load(_db, AsOf);
        var mags = Assert.Single(resolver.ResolveAll(), h => h.ItemClass == Mag);

        Assert.Equal(31, mags.Quantity);
        Assert.True(mags.IsAtStation);
        Assert.Equal(Station, mags.Root!.Key);
        Assert.Equal(ItemOrigin.Bought, mags.Origin);
        Assert.Equal(Confidence.High, mags.Confidence);

        // The test log never names the station; nothing else about the stack is in doubt.
        Assert.Equal(["this station's id was never paired with a name"], mags.Caveats);
        Assert.Equal(31, resolver.ReplayStats.UnitsBought);
    }

    [Fact]
    public void Bought_items_stay_bought_when_moved_and_when_the_game_names_one()
    {
        Ingest([Arrived, Bought, Answered, .. TakenOut]);

        var resolver = HoldingResolver.Load(_db, AsOf);
        var rows = resolver.ResolveAll().Where(h => h.ItemClass == Mag).ToList();

        // The whole stack was found at the station: nothing about it is a guess.
        Assert.Equal(0, resolver.ReplayStats.UnitsInferred);
        Assert.DoesNotContain(rows, h => h.Holder.Kind == InventoryKind.Location);

        var inBox = Assert.Single(rows, h => h.Holder == new Holding(InventoryKind.Container, Box));
        Assert.Equal(30, inBox.Quantity);
        Assert.Equal(ItemOrigin.Bought, inBox.Origin);

        var equipped = resolver.Resolve("945901578009");
        Assert.NotNull(equipped);
        Assert.Equal(InventoryKind.Equipped, equipped.Holder.Kind);
        Assert.Equal(ItemOrigin.Bought, equipped.Origin);
    }

    [Fact]
    public void An_item_never_seen_bought_has_no_origin()
    {
        // Without the purchase, the same stack turns up out of a station the tracker knew
        // nothing of: first seen, and nothing says how it was come by.
        Ingest([Arrived, .. TakenOut]);

        var resolver = HoldingResolver.Load(_db, AsOf);
        var inBox = Assert.Single(resolver.ResolveAll(), h => h.ItemClass == Mag && h.Holder.Kind == InventoryKind.Container);

        Assert.Equal(ItemOrigin.Unknown, inBox.Origin);
        Assert.Contains(inBox.Caveats, c => c.StartsWith("first seen being moved out", StringComparison.Ordinal));
    }

    [Fact]
    public void The_purchase_is_listed_with_its_shop_price_and_place()
    {
        Ingest(Arrived, Bought, Answered);

        var resolver = HoldingResolver.Load(_db, AsOf);
        var purchase = Assert.Single(resolver.Purchases(Mag));

        Assert.Equal(DateTimeOffset.Parse("2026-11-22T12:50:25.311Z"), purchase.At);
        Assert.Equal(Shop, purchase.Shop);
        Assert.Equal(31, purchase.Quantity);
        Assert.Equal(9083m, purchase.Price);
        Assert.NotNull(purchase.PlaceLabel);
        Assert.Empty(resolver.Purchases("behr_rifle_ballistic_03"));
    }

    [Fact]
    public void A_purchase_the_shop_never_answered_is_placed_but_not_confirmed()
    {
        Ingest(Arrived, Bought);

        var mags = Assert.Single(HoldingResolver.Load(_db, AsOf).ResolveAll(), h => h.ItemClass == Mag);

        Assert.Equal(ItemOrigin.Bought, mags.Origin);
        Assert.Contains("the game never confirmed the move that put it here", mags.Caveats);
    }

    [Fact]
    public void A_purchase_the_shop_refused_is_neither_placed_nor_listed()
    {
        // Every answer in the logs is a success; this one is made up from the real line.
        var refused = Answered.Replace("result[Success]", "result[Failure]", StringComparison.Ordinal);
        Ingest(Arrived, Bought, refused);

        var resolver = HoldingResolver.Load(_db, AsOf);

        Assert.DoesNotContain(resolver.ResolveAll(), h => h.ItemClass == Mag);
        Assert.Empty(resolver.Purchases(Mag));
    }

    [Fact]
    public void An_answer_from_another_shop_settles_nothing()
    {
        var elsewhere = Answered.Replace("shopId[926576523088]", "shopId[926494536312]", StringComparison.Ordinal);
        var stats = Ingest(Arrived, Bought, elsewhere);

        Assert.Equal(0, stats.PurchasesAnswered);
    }

    [Fact]
    public void A_purchase_with_no_known_place_is_listed_but_put_nowhere()
    {
        var stats = Ingest(Bought, Answered);

        Assert.Equal(1, stats.PurchasesRecorded);
        var resolver = HoldingResolver.Load(_db, AsOf);
        Assert.DoesNotContain(resolver.ResolveAll(), h => h.ItemClass == Mag);

        var purchase = Assert.Single(resolver.Purchases(Mag));
        Assert.Null(purchase.PlaceLabel);
    }

    [Fact]
    public void Reading_the_same_log_twice_records_the_purchase_once()
    {
        Ingest(Arrived, Bought, Answered);
        var again = new LogIngestor(_db, _dir).IngestAll();

        Assert.Equal(0, again.PurchasesRecorded);
        Assert.Single(HoldingResolver.Load(_db, AsOf).Purchases(Mag));
    }
}
