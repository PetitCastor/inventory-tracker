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

    private static readonly Holding OnPlayer = new(InventoryKind.Equipped, "");

    /// <summary>An Interaction with action[Carry]: out of a grid into the player's hand.</summary>
    private static MoveRecord Carry(Holding source, string itemClass, string geid, int minute) =>
        Move(source, OnPlayer, itemClass, geid: geid, minute: minute, moveType: "Interaction") with { Action = "Carry" };

    private static LedgerReplay.BodyEnumeration Enumerated(int minute, params string[] geids) =>
        new(new DateTimeOffset(2026, 7, 16, 12, minute, 0, TimeSpan.Zero), geids.ToHashSet(), new HashSet<string>());

    [Fact]
    public void Carrying_a_counted_item_takes_it_out_of_its_container()
    {
        // The drinks went in by class; the one carried out is named. Without spending the
        // unnamed unit, the crate would still count the drink the player is holding.
        var ledger = Run(
            Move(Nowhere, Crate, "Drink_bottle_cruz_01_a", amount: 2, minute: 1),
            Carry(Crate, "Drink_bottle_cruz_01_a", "780590795163", minute: 2));

        var (_, _, loose) = Assert.Single(ledger.Contents(Crate));
        Assert.Equal(1, loose!.Quantity);
        Assert.Equal(OnPlayer, ledger.InstanceAt["780590795163"]);
    }

    [Fact]
    public void A_carried_item_stored_back_is_in_its_container_once()
    {
        var ledger = Run(
            Move(Nowhere, Crate, "Drink_bottle_cruz_01_a", minute: 1),
            Carry(Crate, "Drink_bottle_cruz_01_a", "780590795163", minute: 2),
            Move(Nowhere, Crate, "Drink_bottle_cruz_01_a", geid: "780590795163", minute: 3, moveType: "Store"));

        var (_, named, loose) = Assert.Single(ledger.Contents(Crate));
        Assert.Single(named);
        Assert.Null(loose);
        Assert.Empty(ledger.Contents(OnPlayer));
    }

    [Fact]
    public void A_carried_item_missing_from_the_next_body_enumeration_was_used_up()
    {
        var ledger = LedgerReplay.Run(
            [Carry(Crate, "Food_Grub_Seanut_1_Lemon", "750982316286", minute: 2)],
            [],
            Enumerated(minute: 10, "200000000218"));

        Assert.Empty(ledger.Contents(OnPlayer));
        Assert.False(ledger.InstanceAt.ContainsKey("750982316286"));
        Assert.Equal(1, ledger.Stats.CarriedUsedUp);
    }

    [Fact]
    public void A_carried_item_still_listed_on_the_body_stays_on_the_player()
    {
        var ledger = LedgerReplay.Run(
            [Carry(Crate, "Drink_bottle_cruz_01_a", "780590795163", minute: 2)],
            [],
            Enumerated(minute: 10, "780590795163"));

        Assert.Equal(OnPlayer, ledger.InstanceAt["780590795163"]);
    }

    [Fact]
    public void A_carry_after_the_newest_enumeration_is_not_contradicted_yet()
    {
        var ledger = LedgerReplay.Run(
            [Carry(Crate, "Drink_bottle_cruz_01_a", "780590795163", minute: 12)],
            [],
            Enumerated(minute: 10));

        Assert.Equal(OnPlayer, ledger.InstanceAt["780590795163"]);
    }

    [Fact]
    public void A_shortfall_at_the_source_is_drawn_from_what_the_player_carries()
    {
        // Magazines ride along in a backpack and turn up in a station stack with no move line
        // in between. The station stack of 14 is the backpack's 13 plus the 1 already there.
        var ledger = LedgerReplay.Run(
            [
                Move(Nowhere, Backpack, "behr_rifle_ballistic_03_mag", amount: 13, minute: 1),
                Move(Nowhere, Station, "behr_rifle_ballistic_03_mag", amount: 1, minute: 2),
                Move(Station, Crate, "behr_rifle_ballistic_03_mag", amount: 14, minute: 3),
            ],
            [],
            carriedContainers: new HashSet<string> { Backpack.Key });

        Assert.Empty(ledger.Contents(Backpack));
        var (_, _, loose) = Assert.Single(ledger.Contents(Crate));
        Assert.Equal(14, loose!.Quantity);
        Assert.False(loose.Inferred);
        Assert.Equal(13, ledger.Stats.UnitsFromCarried);
    }

    [Fact]
    public void A_container_that_is_not_carried_is_never_raided_for_a_shortfall()
    {
        // A crate at another station does not travel with the player, so its contents are
        // not a plausible source — the arrival stays a guess, and a flagged one.
        var ledger = LedgerReplay.Run(
            [
                Move(Nowhere, OtherStation, "rifle_01", minute: 1),
                Move(Crate, Station, "rifle_01", minute: 2),
            ],
            []);

        Assert.Single(ledger.Contents(OtherStation));
        var (_, _, loose) = Assert.Single(ledger.Contents(Station));
        Assert.True(loose!.Inferred);
        Assert.True(loose.DuplicateRisk);
        Assert.Equal(1, ledger.Stats.UnitsDuplicateRisk);
    }

    [Fact]
    public void A_class_level_store_takes_the_item_off_the_player()
    {
        // A Store's source is always the player's hand or body, and the log leaves it INVALID.
        // Without reading it that way, the canister stored in the crate stays equipped too.
        var ledger = Run(
            Move(Station, OnPlayer, "grin_multitool_resource_salvage_repair_01_filled", minute: 1, moveType: "Interaction"),
            Move(Nowhere, Crate, "grin_multitool_resource_salvage_repair_01_filled", minute: 2, moveType: "Store"));

        Assert.Empty(ledger.Contents(OnPlayer));
        var (_, _, loose) = Assert.Single(ledger.Contents(Crate));
        Assert.False(loose!.Inferred);
    }

    [Fact]
    public void An_arrival_of_a_class_held_nowhere_else_is_a_first_sighting_not_a_duplicate()
    {
        // Armour pulled out of a wreck's crate: the crate was never seen filled, and nothing
        // of the class is recorded anywhere. It is loot, not a double count.
        var ledger = Run(Move(Crate, Station, "srvl_armor_heavy_core_01_01_10blue", minute: 1));

        var (_, _, loose) = Assert.Single(ledger.Contents(Station));
        Assert.True(loose!.Inferred);
        Assert.False(loose.DuplicateRisk);
        Assert.Equal(1, ledger.Stats.UnitsFirstSeen);
        Assert.Equal(1.0, ledger.Stats.AccountedRate);
    }

    [Fact]
    public void An_equipped_item_missing_from_the_enumeration_is_left_alone()
    {
        // Only carried items are ever used up. Absence of an equipped item has other
        // explanations, and demoting those is a separate decision.
        var ledger = LedgerReplay.Run(
            [Move(Station, OnPlayer, "rsi_helmet_01", geid: "333333333333", minute: 2, moveType: "Interaction")],
            [],
            Enumerated(minute: 10));

        Assert.Equal(OnPlayer, ledger.InstanceAt["333333333333"]);
    }
}
