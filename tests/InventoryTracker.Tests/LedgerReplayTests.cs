using InventoryTracker.Model;
using InventoryTracker.Resolve;

namespace InventoryTracker.Tests;

/// <summary>
/// The ledger is an order-dependent fold over a delta stream with no snapshot to anchor to,
/// so the interesting behaviour is what happens when a class-level move meets a named one.
/// </summary>
public class LedgerReplayTests
{
    private static readonly Holding Station = new(InventoryKind.Location, "4005457614");
    private static readonly Holding OtherStation = new(InventoryKind.Location, "9999999999");
    private static readonly Holding Crate = new(InventoryKind.Container, "681562156430");

    /// <summary>A source that is not a real holding — "we never saw where it came from".</summary>
    private static readonly Holding Nowhere = new(InventoryKind.Invalid, "");

    private static int _id;

    private static MoveRecord Move(
        Holding source,
        Holding target,
        string itemClass,
        string? geid = null,
        int amount = 1,
        string? result = "succeed",
        int minute = 0,
        string moveType = "Move") =>
        new(
            ++_id,
            new DateTimeOffset(2026, 7, 16, 12, minute, 0, TimeSpan.Zero),
            moveType,
            itemClass,
            geid,
            amount,
            source.Kind,
            source.Key,
            target.Kind,
            target.Key,
            target.Key,
            result);

    private static LedgerReplay Run(params MoveRecord[] moves) =>
        LedgerReplay.Run(moves, [], new HashSet<string>());

    [Fact]
    public void Places_a_named_entity_where_its_last_move_put_it()
    {
        var ledger = Run(
            Move(Nowhere, Station, "rifle_01", geid: "111111111111", minute: 1),
            Move(Station, Crate, "rifle_01", geid: "111111111111", minute: 2));

        Assert.Equal(Crate, ledger.InstanceAt["111111111111"]);
        Assert.Empty(ledger.NamedIn(Station, "rifle_01"));
        Assert.Single(ledger.NamedIn(Crate, "rifle_01"));
    }

    [Fact]
    public void Ignores_a_move_the_game_said_failed()
    {
        var ledger = Run(
            Move(Nowhere, Station, "rifle_01", geid: "111111111111", minute: 1),
            Move(Station, Crate, "rifle_01", geid: "111111111111", minute: 2, result: "failed"));

        Assert.Equal(Station, ledger.InstanceAt["111111111111"]);
    }

    [Fact]
    public void Counts_unnamed_units_that_the_log_only_describes_by_class()
    {
        var ledger = Run(Move(Nowhere, Station, "ammo_box_01", amount: 5, minute: 1));

        var (_, named, loose) = Assert.Single(ledger.Contents(Station));
        Assert.Empty(named);
        Assert.NotNull(loose);
        Assert.Equal(5, loose.Quantity);
    }

    [Fact]
    public void A_class_level_move_relocates_a_named_instance_sitting_at_its_source()
    {
        // This is what makes an item disappear from the station it was stored at once the
        // player picks it back up: the pickup is logged by class, not by entity.
        var ledger = Run(
            Move(Nowhere, Station, "rifle_01", geid: "111111111111", minute: 1),
            Move(Station, Crate, "rifle_01", minute: 2));

        Assert.Equal(Crate, ledger.InstanceAt["111111111111"]);
        Assert.Empty(ledger.NamedIn(Station, "rifle_01"));
    }

    [Fact]
    public void Credits_the_destination_when_the_source_cannot_account_for_the_units()
    {
        // History starts mid-stream all the time; the units existed, we just never saw them
        // arrive. The row is flagged inferred rather than dropped.
        var ledger = Run(Move(OtherStation, Station, "medpen_01", minute: 1));

        var (_, _, loose) = Assert.Single(ledger.Contents(Station));
        Assert.NotNull(loose);
        Assert.Equal(1, loose.Quantity);
        Assert.True(loose.Inferred);
    }

    [Fact]
    public void Collapses_a_guess_into_the_named_entity_that_turns_out_to_be_it()
    {
        // An item dragged in by class and then stored by name is one thing described twice.
        // Without the reconcile the guess lingers as a phantom after the named item moves on.
        var ledger = Run(
            Move(OtherStation, Station, "rifle_01", minute: 1),
            Move(Nowhere, Station, "rifle_01", geid: "111111111111", minute: 2));

        var (_, named, loose) = Assert.Single(ledger.Contents(Station));
        Assert.Single(named);
        Assert.Null(loose);
    }

    [Fact]
    public void A_worn_sighting_overrides_wherever_the_moves_left_the_item()
    {
        // An attachment line is positive proof the item is on the player, which no move can
        // give.
        var ledger = LedgerReplay.Run(
            [Move(Nowhere, Station, "undersuit_01", geid: "222222222222", minute: 1)],
            [new LedgerReplay.WornSighting(
                new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero), "222222222222", "undersuit_01")],
            new HashSet<string>());

        Assert.Equal(InventoryKind.Equipped, ledger.InstanceAt["222222222222"].Kind);
    }

    [Fact]
    public void A_move_into_a_backpack_leaves_the_item_where_it_was()
    {
        // The backpack travels with the player, so landing in it says nothing about where
        // the player is.
        var ledger = LedgerReplay.Run(
            [
                Move(Nowhere, Station, "rifle_01", geid: "111111111111", minute: 1),
                Move(Station, Crate, "rifle_01", geid: "111111111111", minute: 2),
            ],
            [],
            new HashSet<string> { Crate.Key });

        Assert.Equal(Station, ledger.InstanceAt["111111111111"]);
    }
}
