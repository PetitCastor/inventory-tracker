using InventoryTracker.Ingest;
using InventoryTracker.Model;

namespace InventoryTracker.Tests;

/// <summary>
/// Lines here are real 4.9 Game.log records, trimmed of the player name only. The parser is
/// 13 regexes over an undocumented grammar, so a silent stop-matching is the failure that
/// matters: it shows up as "my stuff vanished", never as a build error.
/// </summary>
public class InventoryEventParserTests
{
    private static LogLine Parse(string raw)
    {
        Assert.True(LogLine.TryParse(raw, out var line), $"line did not match the log shape: {raw}");
        return line;
    }

    private static InventoryEvent? Event(string raw) => InventoryEventParser.Parse(Parse(raw));

    [Fact]
    public void Reads_a_queued_store_as_an_instance_level_move()
    {
        var ev = Event(
            "<2026-07-16T11:20:52.355Z> [Notice] <InventoryManagementRequest> Queued Request[12] " +
            "Type[Store] for 'Pilot' [2001] Source Inventory[INVALID] " +
            "Target Inventory[204821708183:Location:4005457614]. " +
            "Source[NONE] amount[1] rank[0]. Target[NONE] amount[0] rank[0]. " +
            "Item[grin_multitool_01_red01_481104483777] action[none]");

        var move = Assert.IsType<ItemMoved>(ev);
        Assert.Equal("Store", move.MoveType);
        Assert.Equal("grin_multitool_01_red01", move.ItemClass);
        Assert.Equal("481104483777", move.ItemGeid);
        Assert.Equal(InventoryKind.Location, move.Target.Kind);
        Assert.Equal("4005457614", move.Target.HoldingKey);
    }

    [Fact]
    public void Falls_back_to_the_source_class_when_no_entity_is_named()
    {
        // Everything except Store reports Item[NONE]; the class comes from Source[].
        var ev = Event(
            "<2026-07-16T11:21:00.000Z> [Notice] <InventoryManagementRequest> Queued Request[13] " +
            "Type[Move] for 'Pilot' [2001] Source Inventory[204821708183:Location:4005457614] " +
            "Target Inventory[681562156430:Container:0]. " +
            "Source[ammo_box_ballistic_01] amount[3] rank[0]. Target[NONE] amount[0] rank[0]. " +
            "Item[NONE] action[none]");

        var move = Assert.IsType<ItemMoved>(ev);
        Assert.Equal("ammo_box_ballistic_01", move.ItemClass);
        Assert.Null(move.ItemGeid);
        Assert.Equal(3, move.Amount);
        Assert.Equal(InventoryKind.Container, move.Target.Kind);
        Assert.Equal("681562156430", move.Target.HoldingKey);
    }

    [Theory]
    [InlineData("QueryInventory")]
    [InlineData("OpenNestedInventory")]
    [InlineData("Sort")]
    [InlineData("StackAll")]
    public void Ignores_move_types_that_relocate_nothing(string moveType)
    {
        var ev = Event(
            $"<2026-07-16T11:21:00.000Z> [Notice] <InventoryManagementRequest> Queued Request[14] " +
            $"Type[{moveType}] for 'Pilot' [2001] Source Inventory[204821708183:Location:4005457614] " +
            "Target Inventory[681562156430:Container:0]. " +
            "Source[some_item_01] amount[1] rank[0]. Target[NONE] amount[0] rank[0]. " +
            "Item[NONE] action[none]");

        Assert.Null(ev);
    }

    [Fact]
    public void Expands_a_multi_select_drag_into_one_batch()
    {
        // A bracketed ItemClass list is the only record of a multi-select drag: the Queued
        // counterpart degenerates to Source[NULL] / Item[NONE].
        var ev = Event(
            "<2026-07-16T11:22:00.000Z> [Notice] <Add Inventory Management Move> New request[21] " +
            "Player[Pilot] Type[Move] SourceInventory[204821708183:Location:4005457614] " +
            "TargetInventory[681562156430:Container:0] " +
            "ItemClass[[medpen_01] [ammo_box_01] [drink_water_01] ] StoredEntity[NONE] Caller[DragDrop]");

        var batch = Assert.IsType<MoveBatchRequested>(ev);
        Assert.Equal(["medpen_01", "ammo_box_01", "drink_water_01"], batch.ItemClasses);
    }

    [Fact]
    public void Does_not_truncate_a_bracketed_class_list_at_the_first_bracket()
    {
        // Stopping at the first ']' would silently reduce the list to "[medpen_01".
        var ev = Event(
            "<2026-07-16T11:22:00.000Z> [Notice] <Add Inventory Management Move> New request[22] " +
            "Player[Pilot] Type[Move] SourceInventory[INVALID] " +
            "TargetInventory[681562156430:Container:0] " +
            "ItemClass[[medpen_01] [ammo_box_01] ] StoredEntity[NONE] Caller[DragDrop]");

        var batch = Assert.IsType<MoveBatchRequested>(ev);
        Assert.Equal(2, batch.ItemClasses.Count);
    }

    [Fact]
    public void Reads_a_single_class_move_request_with_its_caller()
    {
        var ev = Event(
            "<2026-07-16T11:23:00.000Z> [Notice] <Add Inventory Management Move> New request[31] " +
            "Player[Pilot] Type[Interaction] SourceInventory[204821708183:Location:4005457614] " +
            "TargetInventory[INVALID] ItemClass[behr_pistol_01] StoredEntity[NONE] " +
            "Caller[CSCItemAttachmentManager::AttachItem]");

        var req = Assert.IsType<MoveRequested>(ev);
        Assert.Equal("behr_pistol_01", req.ItemClass);
        Assert.Contains("AttachItem", req.Caller);
    }

    [Fact]
    public void Reads_a_container_identity_from_token_flow()
    {
        var ev = Event(
            "<2026-07-16T11:24:00.000Z> [Notice] <Inventory Token Flow> moved 1 token on " +
            "Inventory[qrt_utility_heavy_backpack_01_01_03_745829874584, 745829874584]");

        var container = Assert.IsType<ContainerIdentified>(ev);
        Assert.Equal("745829874584", container.Geid);
        Assert.Equal("qrt_utility_heavy_backpack_01_01_03", container.ClassName);
    }

    [Fact]
    public void Reads_both_capacities_from_a_drag()
    {
        var ev = Event(
            "<2026-07-16T11:25:00.000Z> [Notice] <OnDragInventoryItemModifyTarget> " +
            "Source[0:ClientOnly:1] Capacity[196000] more | " +
            "Target[681562156430:Container:0] Capacity[2000000] more");

        var cap = Assert.IsType<ContainerCapacitySeen>(ev);
        Assert.Equal(196000, cap.SourceCapacity);
        Assert.Equal(2_000_000, cap.TargetCapacity);
        Assert.Equal("681562156430", cap.Target.HoldingKey);
    }

    [Fact]
    public void Reads_a_worn_attachment()
    {
        var ev = Event(
            "<2026-07-16T11:26:00.000Z> [Notice] <AttachmentReceived> Player[Pilot] " +
            "Attachment[qrt_undersuit_01_745829874584, qrt_undersuit_01, 745829874584] " +
            "Status[persistent] Port[Armor_Undersuit]");

        var worn = Assert.IsType<AttachmentSeen>(ev);
        Assert.Equal("745829874584", worn.Geid);
        Assert.Equal("Armor_Undersuit", worn.Port);
    }

    [Fact]
    public void Reads_a_location_change()
    {
        var ev = Event(
            "<2026-07-16T11:27:00.000Z> [Notice] <Update Inventory Location> " +
            "Landing [0] -> [141810852]. Location [0] -> [4005457614]");

        var change = Assert.IsType<LocationChanged>(ev);
        Assert.Equal("141810852", change.NewLandingId);
        Assert.Equal("4005457614", change.NewLocationId);
    }

    [Fact]
    public void Reads_a_location_name()
    {
        var ev = Event(
            "<2026-07-16T11:27:01.000Z> [Notice] <RequestLocationInventory> " +
            "requesting Location[RR_JP_NyxCastra] now");

        var named = Assert.IsType<LocationNamed>(ev);
        Assert.Equal("RR_JP_NyxCastra", named.Name);
    }

    [Fact]
    public void Rejects_an_empty_location_name()
    {
        // Binding an id to "" would lock out the real name later, because a second and
        // different name is treated as a conflict rather than a correction.
        var ev = Event(
            "<2026-07-16T11:27:02.000Z> [Notice] <RequestLocationInventory> requesting Location[] now");

        Assert.Null(ev);
    }

    [Fact]
    public void Reads_the_player_facing_place_name_from_a_route()
    {
        var ev = Event(
            "<2026-07-16T11:28:00.000Z> [Notice] <Calculate Route> stuff " +
            "|Projected Start Location is Stanton Gateway for route to destination Area18");

        var place = Assert.IsType<PlaceNamed>(ev);
        Assert.Equal("Stanton Gateway", place.DisplayName);
    }

    [Fact]
    public void Reads_the_system_from_a_streamed_object_container_path()
    {
        var ev = Event(
            "<2026-07-16T11:29:00.000Z> [Notice] <CIG streaming> loading " +
            "Data/objectcontainers/pu/loc/mod/nyx/station/ser/reststop_ext/rs_ext_nyx-castra_jp1.socpak");

        var asset = Assert.IsType<PlaceAssetSeen>(ev);
        Assert.Equal("Nyx", asset.System);
    }

    [Fact]
    public void Ignores_a_line_it_has_no_rule_for()
    {
        Assert.Null(Event("<2026-07-16T11:30:00.000Z> [Notice] <Something Else> nothing of interest"));
    }

    // Build 12519617 (late Aug 2026) renamed the queued-move tag and capitalised the
    // Add-Move "New request[" token, with the rest of both line shapes byte-identical to the
    // 4.9 grammar above. Both eras must go on parsing: the third-era lines below are real
    // records (trimmed of the player name only), and the pre-existing tests above must keep
    // passing unchanged.

    [Fact]
    public void Reads_a_third_era_queued_store_under_the_renamed_tag()
    {
        var ev = Event(
            "<2026-08-27T00:41:57.775Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[6] " +
            "Type[Store] for 'Arcadiius' [204821708183] Source Inventory[INVALID] " +
            "Target Inventory[204821708183:Location:2273540638]. " +
            "Source[NULL] amount[0] rank[]. Target[NULL] amount[0] rank[amrgzkwktuhbu]. " +
            "Item[klwe_pistol_energy_01_mag_783802033953] action[None]. " +
            "RequestInProgress[0] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]");

        var move = Assert.IsType<ItemMoved>(ev);
        Assert.Equal("Store", move.MoveType);
        Assert.Equal("klwe_pistol_energy_01_mag", move.ItemClass);
        Assert.Equal("783802033953", move.ItemGeid);
        Assert.Equal(InventoryKind.Location, move.Target.Kind);
        Assert.Equal("2273540638", move.Target.HoldingKey);
    }

    [Fact]
    public void Reads_a_third_era_queued_interaction_at_class_level()
    {
        var ev = Event(
            "<2026-08-27T00:41:57.775Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[11] " +
            "Type[Interaction] for 'Arcadiius' [204821708183] " +
            "Source Inventory[204821708183:Location:2273540638] Target Inventory[INVALID]. " +
            "Source[cds_combat_superheavy_suit_01_04_01] amount[1] rank[amrgzkwktuhbn]. " +
            "Target[NULL] amount[0] rank[]. Item[NONE] action[None]. " +
            "RequestInProgress[0] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]");

        var move = Assert.IsType<ItemMoved>(ev);
        Assert.Equal("Interaction", move.MoveType);
        Assert.Equal("cds_combat_superheavy_suit_01_04_01", move.ItemClass);
        Assert.Null(move.ItemGeid);
    }

    [Fact]
    public void Reads_a_third_era_add_move_with_its_capitalised_request_token()
    {
        var ev = Event(
            "<2026-08-27T00:41:57.774Z> [Notice] <Add Inventory Management Move> New Request[11] " +
            "Player[Arcadiius] Type[Interaction] SourceInventory[204821708183:Location:2273540638] " +
            "TargetInventory[INVALID] ItemClass[cds_combat_superheavy_suit_01_04_01] StoredEntity[NULL] " +
            "LocallyDetached[No] LocalAttached[NULL, Body_ItemPort:Armor_Undersuit] " +
            "PendingMoves[6,  6 7 8 9 10] Caller[CSCLocalPlayerPersonalThoughtComponent::AttachItem] " +
            "[Team_CoreGameplayFeatures][Inventory]");

        var req = Assert.IsType<MoveRequested>(ev);
        Assert.Equal("cds_combat_superheavy_suit_01_04_01", req.ItemClass);
        Assert.Contains("AttachItem", req.Caller);
    }

    [Fact]
    public void Flags_an_unknown_future_queued_tag_as_an_unrecognised_move()
    {
        // A made-up tag one grammar change further than the one we know about. The body
        // still opens with "Queued Request[N]", so this must not be silently dropped.
        var ev = Event(
            "<2026-09-01T00:00:00.000Z> [Notice] <Inventory Mgmt Request Queued V2> " +
            "Queued Request[9] Type[Store] for 'Pilot' [2001] Source Inventory[INVALID] " +
            "Target Inventory[204821708183:Location:2273540638]. " +
            "Source[NULL] amount[0] rank[]. Target[NULL] amount[0] rank[]. " +
            "Item[some_item_01_123456789012] action[None]");

        var flagged = Assert.IsType<UnrecognisedMove>(ev);
        Assert.Equal("Inventory Mgmt Request Queued V2", flagged.EventName);
    }

    [Fact]
    public void Does_not_flag_a_non_relocating_queued_line_under_the_renamed_tag()
    {
        // Correct behaviour, not drift: QueryInventory never relocates anything, in either era.
        var ev = Event(
            "<2026-08-27T00:00:00.000Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[14] " +
            "Type[QueryInventory] for 'Pilot' [2001] Source Inventory[204821708183:Location:4005457614] " +
            "Target Inventory[681562156430:Container:0]. " +
            "Source[some_item_01] amount[1] rank[0]. Target[NONE] amount[0] rank[0]. " +
            "Item[NONE] action[none]");

        Assert.Null(ev);
    }

    [Fact]
    public void Reads_a_store_of_an_entity_the_game_never_gave_an_id()
    {
        // Item[] carries a bare class and Source[] is empty: the item still went in. The
        // reliability study caught four of these dropped in one real session.
        var ev = Event(
            "<2026-08-04T14:31:15.467Z> [Notice] <InventoryManagementRequest> Queued Request[20] Type[Store] " +
            "for 'Pilot' [204821708183] Source Inventory[INVALID] Target Inventory[746335892394:Container:0]. " +
            "Source[NULL] amount[0] rank[]. Target[NULL] amount[0] rank[amqxvpysnesgu]. " +
            "Item[grin_multitool_resource_salvage_repair_01_filled] action[None]. RequestInProgress[0] CurrentProcess[]");

        var move = Assert.IsType<ItemMoved>(ev);
        Assert.Equal("grin_multitool_resource_salvage_repair_01_filled", move.ItemClass);
        Assert.Null(move.ItemGeid);
        Assert.Equal("746335892394", move.Target.HoldingKey);
    }

    [Fact]
    public void Still_drops_a_queued_line_that_names_nothing_at_all()
    {
        // A multi-select drag's Queued half: the Add-move line carries the batch instead.
        var ev = Event(
            "<2026-08-05T11:36:16.710Z> [Notice] <InventoryManagementRequest> Queued Request[14] Type[Move] " +
            "for 'Pilot' [204821708183] Source Inventory[681562156430:Container:0] Target Inventory[751277096094:Container:0]. " +
            "Source[NULL] amount[0] rank[]. Target[NULL] amount[0] rank[]. Item[NONE] action[None].");

        Assert.Null(ev);
    }

    [Theory]
    [InlineData("<Add Inventory Management Move> New Request[29] Player[Pilot] Type[Interaction] SourceInventory[x]", 29, "Interaction", true)]
    [InlineData("<Add Inventory Management Move> New request[3] Player[Pilot] Type[Drop] SourceInventory[x]", 3, "Drop", true)]
    [InlineData("<Some Future Tag> Queued Request[7] Type[Move] for 'Pilot' FromInventory[1:Location:2]", 7, "Move", false)]
    public void Reads_the_shape_of_any_request_line_even_one_it_cannot_parse(
        string body, int requestNo, string moveType, bool isAddMove)
    {
        var shape = InventoryEventParser.ReadRequestShape(Parse($"<2026-09-30T10:00:00.000Z> [Notice] {body}"));

        Assert.NotNull(shape);
        Assert.Equal(requestNo, shape.Value.RequestNo);
        Assert.Equal(moveType, shape.Value.MoveType);
        Assert.Equal(isAddMove, shape.Value.IsAddMove);
    }
}
