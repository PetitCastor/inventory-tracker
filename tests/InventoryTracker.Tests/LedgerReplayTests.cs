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

    /// <summary>A container that happens to be a backpack. The ledger itself has no notion of
    /// "backpack" any more — that classification lives in HoldingResolver, which decides how
    /// to present a container's chain — so as far as LedgerReplay is concerned this is just
    /// another container geid.</summary>
    private static readonly Holding Backpack = new(InventoryKind.Container, "555566667777");

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
        LedgerReplay.Run(moves, []);

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
                new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero), "222222222222", "undersuit_01")]);

        Assert.Equal(InventoryKind.Equipped, ledger.InstanceAt["222222222222"].Kind);
    }

    [Fact]
    public void A_move_into_a_backpack_takes_the_item_off_the_station()
    {
        // The ledger does not know or care that the target is a backpack — that
        // classification is HoldingResolver's job, applied when it presents the chain, not
        // the ledger's when it folds moves. A move into any real container takes the item off
        // wherever it was, backpack included; a class-level pickup out of it later must find
        // it here to avoid double-counting (see the test below).
        var ledger = Run(
            Move(Nowhere, Station, "rifle_01", geid: "111111111111", minute: 1),
            Move(Station, Backpack, "rifle_01", geid: "111111111111", minute: 2));

        Assert.Equal(Backpack, ledger.InstanceAt["111111111111"]);
        Assert.Empty(ledger.NamedIn(Station, "rifle_01"));
        Assert.Single(ledger.NamedIn(Backpack, "rifle_01"));
    }

    [Fact]
    public void Moving_an_item_out_of_a_backpack_by_class_does_not_duplicate_it()
    {
        // Dropping the backpack special-case (reversing 708e0d9) means the named rifle
        // actually lands in the ledger's idea of the backpack when it is stored there. That is
        // what lets the later class-level pickup — logged by class only, the game never says
        // which item was grabbed — find the named instance already sitting at its source
        // instead of inventing a second, anonymous one at the target.
        var ledger = Run(
            Move(Nowhere, Station, "rifle_01", geid: "111111111111", minute: 1),
            Move(Station, Backpack, "rifle_01", geid: "111111111111", minute: 2),
            Move(Backpack, OtherStation, "rifle_01", minute: 3));

        Assert.Equal(OtherStation, ledger.InstanceAt["111111111111"]);

        var allNamed = new[] { Station, Backpack, OtherStation }
            .SelectMany(h => ledger.NamedIn(h, "rifle_01"))
            .ToList();
        var allLoose = new[] { Station, Backpack, OtherStation }
            .SelectMany(h => ledger.Contents(h))
            .Where(c => c.ItemClass == "rifle_01")
            .Select(c => c.Loose)
            .Where(l => l is { Quantity: > 0 })
            .ToList();

        var namedInstance = Assert.Single(allNamed);
        Assert.Equal("111111111111", namedInstance.Geid);
        Assert.Single(ledger.NamedIn(OtherStation, "rifle_01"));
        Assert.Empty(allLoose);
    }
}
