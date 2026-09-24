using InventoryTracker.Ingest;
using InventoryTracker.Model;

namespace InventoryTracker.Tests;

/// <summary>
/// Build 12519617 (2026-08-26) renamed <c>&lt;InventoryManagementRequest&gt;</c> to
/// <c>&lt;Inventory Mgmt Request Queued&gt;</c> and wrote "New Request[" with a capital R.
/// Neither change touched the rest of the line, and together they silently stopped every
/// move from being recorded. The lines below are real records from that build, player name
/// replaced.
/// </summary>
public class LogFormatDriftTests
{
    private static LogLine Line(string raw)
    {
        Assert.True(LogLine.TryParse(raw, out var line), $"line did not match the log shape: {raw}");
        return line;
    }

    private const string QueuedMove =
        "Queued Request[53] Type[Move] for 'Pilot' [204821708183] " +
        "Source Inventory[204821708183:Location:2273540638] Target Inventory[757726468117:Container:0]. " +
        "Source[behr_rifle_ballistic_03_tint03] amount[1] rank[amrhcwpnbzo]. Target[NULL] amount[0] rank[amrhcwvijrncn]. " +
        "Item[NONE] action[None]. RequestInProgress[0] CurrentProcess[]";

    [Theory]
    [InlineData("InventoryManagementRequest")]
    [InlineData("Inventory Mgmt Request Queued")]
    public void Reads_a_queued_move_under_either_tag(string tag)
    {
        var ev = InventoryEventParser.Parse(Line($"<2026-08-27T00:46:52.940Z> [Notice] <{tag}> {QueuedMove}"));

        var move = Assert.IsType<ItemMoved>(ev);
        Assert.Equal("Move", move.MoveType);
        Assert.Equal("behr_rifle_ballistic_03_tint03", move.ItemClass);
        Assert.Equal(InventoryKind.Container, move.Target.Kind);
        Assert.Equal("757726468117", move.Target.HoldingKey);
        Assert.True(InventoryEventParser.IsKnownRequestTag(tag));
    }

    [Fact]
    public void Reads_a_queued_move_filed_under_a_tag_it_has_never_seen()
    {
        // The next rename should cost a warning, not a month of lost moves.
        var line = Line($"<2026-08-27T00:46:52.940Z> [Notice] <Inventory Mgmt Request Enqueued> {QueuedMove}");

        Assert.IsType<ItemMoved>(InventoryEventParser.Parse(line));
        Assert.False(InventoryEventParser.IsKnownRequestTag(line.EventName));
    }

    [Theory]
    [InlineData("New request")]
    [InlineData("New Request")]
    public void Reads_an_add_move_line_whatever_the_case_of_request(string opener)
    {
        var ev = InventoryEventParser.Parse(Line(
            "<2026-08-27T00:43:33.591Z> [Notice] <Add Inventory Management Move> " +
            $"{opener}[29] Player[Pilot] Type[Interaction] SourceInventory[204821708183:Location:2273540638] " +
            "TargetInventory[INVALID] ItemClass[slaver_undersuit_01_01_01] StoredEntity[NULL] LocallyDetached[No] " +
            "LocalAttached[NULL, Body_ItemPort:Armor_Undersuit] PendingMoves[1, ] " +
            "Caller[CSCLocalPlayerPersonalThoughtComponent::AttachItem]"));

        var req = Assert.IsType<MoveRequested>(ev);
        Assert.Equal(29, req.RequestNo);
        Assert.Equal("slaver_undersuit_01_01_01", req.ItemClass);
        Assert.Contains("AttachItem", req.Caller);
    }

    [Fact]
    public void Reads_a_store_of_an_entity_the_game_never_gave_an_id()
    {
        // Item[] carries a bare class and Source[] is empty: the item still went in.
        var ev = InventoryEventParser.Parse(Line(
            "<2026-08-04T14:31:15.467Z> [Notice] <InventoryManagementRequest> Queued Request[20] Type[Store] " +
            "for 'Pilot' [204821708183] Source Inventory[INVALID] Target Inventory[746335892394:Container:0]. " +
            "Source[NULL] amount[0] rank[]. Target[NULL] amount[0] rank[amqxvpysnesgu]. " +
            "Item[grin_multitool_resource_salvage_repair_01_filled] action[None]. RequestInProgress[0] CurrentProcess[]"));

        var move = Assert.IsType<ItemMoved>(ev);
        Assert.Equal("grin_multitool_resource_salvage_repair_01_filled", move.ItemClass);
        Assert.Null(move.ItemGeid);
        Assert.Equal("746335892394", move.Target.HoldingKey);
    }

    [Fact]
    public void Recognises_a_request_line_by_its_shape_even_when_it_cannot_parse_it()
    {
        // A rewording the parsing patterns do not follow still has to be counted as missed.
        var line = Line(
            "<2026-09-30T10:00:00.000Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[7] " +
            "Type[Move] for 'Pilot' [204821708183] FromInventory[1:Location:2] ToInventory[3:Container:0].");

        Assert.Null(InventoryEventParser.Parse(line));
        var shape = InventoryEventParser.ReadRequestShape(line);
        Assert.NotNull(shape);
        Assert.Equal(7, shape.Value.RequestNo);
        Assert.Equal("Move", shape.Value.MoveType);
        Assert.False(shape.Value.IsAddMove);
        Assert.False(shape.Value.IsDegenerate);
    }

    [Fact]
    public void Treats_a_batch_drags_empty_queued_half_as_expected_not_as_drift()
    {
        var line = Line(
            "<2026-08-05T11:36:16.710Z> [Notice] <InventoryManagementRequest> Queued Request[14] Type[Move] " +
            "for 'Pilot' [204821708183] Source Inventory[681562156430:Container:0] Target Inventory[751277096094:Container:0]. " +
            "Source[NULL] amount[0] rank[]. Target[NULL] amount[0] rank[]. Item[NONE] action[None].");

        Assert.Null(InventoryEventParser.Parse(line));
        Assert.True(InventoryEventParser.ReadRequestShape(line)!.Value.IsDegenerate);
    }
}
