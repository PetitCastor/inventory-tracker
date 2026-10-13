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
    public void Changing_a_named_entities_class_and_case_keeps_one_current_placement()
    {
        const string geid = "ENTITY-1";
        const string oldClass = "Rifle_01";
        const string changedClass = "rifle_02";
        var first = Move(Nowhere, Station, oldClass, geid, minute: 1);
        var second = Move(Station, Crate, changedClass, geid, minute: 2);
        var third = Move(Crate, OtherStation, "RIFLE_03", geid, minute: 3);

        AssertCurrent(Run(first), geid, Station, oldClass);
        AssertCurrent(Run(first, second), geid, Crate, changedClass);
        AssertCurrent(Run(first, second, third), geid, OtherStation, "RIFLE_03");
    }

    [Fact]
    public void Changing_only_the_case_of_a_named_entities_class_keeps_one_placement()
    {
        const string geid = "ENTITY-CASE";
        const string originalClass = "Rifle_01";
        const string casedClass = "RIFLE_01";
        var first = Move(Nowhere, Station, originalClass, geid, minute: 1);
        var second = Move(Station, Crate, casedClass, geid, minute: 2);

        AssertCurrent(Run(first), geid, Station, originalClass);
        var ledger = Run(first, second);
        AssertCurrent(ledger, geid, Crate, casedClass);
        Assert.Empty(ledger.NamedIn(Station, originalClass));
    }

    [Fact]
    public void Moving_a_named_entity_by_class_preserves_a_counted_anonymous_unit_at_target()
    {
        const string itemClass = "rifle_01";
        const string geid = "NAMED-SOURCE";
        var first = Move(Nowhere, OtherStation, itemClass, amount: 1, minute: 1);
        var second = Move(OtherStation, Crate, itemClass, amount: 1, minute: 2);
        var third = Move(Nowhere, Station, itemClass, geid, minute: 3);
        var fourth = Move(Station, Crate, itemClass, amount: 1, minute: 4);
        var before = Run(first, second, third);
        AssertTransferPreconditions(before, Crate, Station, itemClass, geid);
        var ledger = Run(
            first, second, third, fourth);

        AssertCountedAndNamedRemain(ledger, Crate, itemClass, geid);
        Assert.Equal(1, ledger.Stats.UnitsFromNamed);
    }

    [Fact]
    public void Moving_a_carried_named_entity_by_class_preserves_a_counted_anonymous_unit_at_target()
    {
        const string itemClass = "rifle_01";
        const string geid = "NAMED-CARRIED";
        var first = Move(Nowhere, OtherStation, itemClass, amount: 1, minute: 1);
        var second = Move(OtherStation, Crate, itemClass, amount: 1, minute: 2);
        var third = Carry(Station, itemClass, geid, minute: 3);
        var fourth = Move(Station, Crate, itemClass, amount: 1, minute: 4);
        var before = Run(first, second, third);
        AssertTransferPreconditions(before, Crate, OnPlayer, itemClass, geid);
        var ledger = Run(first, second, third, fourth);

        AssertCountedAndNamedRemain(ledger, Crate, itemClass, geid);
        Assert.Equal(1, ledger.Stats.UnitsFromCarried);
    }

    [Fact]
    public void Moving_a_lost_track_named_entity_by_class_preserves_a_counted_anonymous_unit_at_target()
    {
        const string itemClass = "rifle_01";
        const string geid = "NAMED-LOST";
        var first = Move(Nowhere, OtherStation, itemClass, amount: 1, minute: 1);
        var second = Move(OtherStation, Crate, itemClass, amount: 1, minute: 2);
        var third = Move(Station, OnPlayer, itemClass, geid, minute: 3, moveType: "Interaction");
        var fourth = Move(Station, Crate, itemClass, amount: 1, minute: 20);
        var before = LedgerReplay.Run(
            [first, second, third],
            [],
            [Enumerated(minute: 10, "ANOTHER-ITEM")]);
        AssertTransferPreconditions(before, Crate, LedgerReplay.LostTrack, itemClass, geid);
        var ledger = LedgerReplay.Run(
            [first, second, third, fourth],
            [],
            [Enumerated(minute: 10, "ANOTHER-ITEM")]);

        AssertCountedAndNamedRemain(ledger, Crate, itemClass, geid);
        Assert.Equal(1, ledger.Stats.WornEvicted);
        Assert.Equal(1, ledger.Stats.UnitsFromLostTrack);
    }

    private static void AssertTransferPreconditions(
        LedgerReplay ledger, Holding target, Holding namedSource, string itemClass, string geid)
    {
        var (_, targetNamed, targetLoose) = Assert.Single(ledger.Contents(target));
        Assert.Empty(targetNamed);
        Assert.Equal(1, targetLoose?.Quantity ?? 0);
        Assert.NotNull(targetLoose);
        Assert.False(targetLoose.Inferred);
        Assert.Equal(geid, Assert.Single(ledger.NamedIn(namedSource, itemClass)).Geid);
        Assert.Equal(namedSource, ledger.InstanceAt[geid]);
    }

    private static void AssertCountedAndNamedRemain(LedgerReplay ledger, Holding target, string itemClass, string geid)
    {
        var contents = Assert.Single(ledger.Contents(target));
        Assert.Equal(itemClass, contents.ItemClass);
        var named = contents.Named;
        var loose = contents.Loose;
        Assert.Equal(geid, Assert.Single(named).Geid);
        var looseQuantity = loose?.Quantity ?? 0;
        Assert.True(
            loose is { Inferred: false } && looseQuantity == 1
                && named.Count + looseQuantity == 2 && ledger.Stats.AnonymousReconciled == 0,
            $"Expected named=1, loose=1 counted, total=2, reconciled=0; got named={named.Count}, " +
            $"loose={looseQuantity}, inferred={loose?.Inferred}, total={named.Count + looseQuantity}, " +
            $"reconciled={ledger.Stats.AnonymousReconciled}");
        Assert.Equal(target, ledger.InstanceAt[geid]);
        Assert.Equal(1, ledger.Occupied.SelectMany(ledger.Contents)
            .SelectMany(contents => contents.Named).Count(i => i.Geid == geid));
    }

    private static void AssertCurrent(LedgerReplay ledger, string geid, Holding holding, string itemClass)
    {
        Assert.Equal(holding, ledger.InstanceAt[geid]);
        var found = ledger.Find(geid);
        Assert.NotNull(found);
        Assert.Equal(itemClass, found.ItemClass);
        Assert.Single(ledger.NamedIn(holding, itemClass));
        Assert.Equal(1, ledger.Occupied.SelectMany(ledger.Contents)
            .SelectMany(contents => contents.Named).Count(i => i.Geid == geid));
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
            [Enumerated(minute: 10, "200000000218")]);

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
            [Enumerated(minute: 10, "780590795163")]);

        Assert.Equal(OnPlayer, ledger.InstanceAt["780590795163"]);
    }

    [Fact]
    public void An_item_carried_then_moved_by_class_is_no_longer_carried()
    {
        // Stored back and taken out again by class-level moves, the drink is on the player
        // as an ordinary item. Missing from an enumeration, it is not used up: it left the
        // player for somewhere no line names.
        var ledger = LedgerReplay.Run(
            [
                Carry(Crate, "Drink_bottle_cruz_01_a", "780590795163", minute: 2),
                Move(Nowhere, Crate, "Drink_bottle_cruz_01_a", minute: 3, moveType: "Store"),
                Move(Crate, OnPlayer, "Drink_bottle_cruz_01_a", minute: 4),
            ],
            [],
            [Enumerated(minute: 10)]);

        Assert.Equal(LedgerReplay.LostTrack, ledger.InstanceAt["780590795163"]);
        Assert.Equal(0, ledger.Stats.CarriedUsedUp);
    }

    [Fact]
    public void A_carry_after_the_newest_enumeration_is_not_contradicted_yet()
    {
        var ledger = LedgerReplay.Run(
            [Carry(Crate, "Drink_bottle_cruz_01_a", "780590795163", minute: 12)],
            [],
            [Enumerated(minute: 10)]);

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
        // not a plausible source — the arrival stays a guess, and a flagged one. The backpack
        // is carried, so the crate is left alone by choice, not for want of a carried set.
        var otherCrate = new Holding(InventoryKind.Container, "681562156431");
        var ledger = LedgerReplay.Run(
            [
                Move(Nowhere, otherCrate, "rifle_01", minute: 1),
                Move(Crate, Station, "rifle_01", minute: 2),
            ],
            [],
            carriedContainers: new HashSet<string> { Backpack.Key });

        Assert.Single(ledger.Contents(otherCrate));
        var (_, _, loose) = Assert.Single(ledger.Contents(Station));
        Assert.True(loose!.Inferred);
        Assert.True(loose.DuplicateRisk);
        Assert.Equal(1, ledger.Stats.UnitsDuplicateRisk);
        Assert.Equal(0, ledger.Stats.UnitsFromCarried);
    }

    [Fact]
    public void A_backpack_stored_in_a_locker_is_not_raided_for_a_shortfall()
    {
        // Apparel travels with the player only while they wear it. Once the backpack itself
        // is stored, its magazines are at the locker, not a source for a stack elsewhere.
        var ledger = LedgerReplay.Run(
            [
                Move(Nowhere, Backpack, "behr_rifle_ballistic_03_mag", amount: 13, minute: 1),
                Move(OnPlayer, Crate, "backpack_class", geid: Backpack.Key, minute: 2),
                Move(Station, OtherStation, "behr_rifle_ballistic_03_mag", amount: 13, minute: 3),
            ],
            [],
            carriedContainers: new HashSet<string> { Backpack.Key });

        var (_, _, kept) = Assert.Single(ledger.Contents(Backpack));
        Assert.Equal(13, kept!.Quantity);
        Assert.Equal(0, ledger.Stats.UnitsFromCarried);
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
    public void An_equipped_item_missing_from_the_enumeration_is_no_longer_on_the_player()
    {
        // Only carried items are ever used up. An equipped item the body no longer lists was
        // stored, sold or lost somewhere no line names; it is kept, but not on the player.
        var ledger = LedgerReplay.Run(
            [Move(Station, OnPlayer, "rsi_helmet_01", geid: "333333333333", minute: 2, moveType: "Interaction")],
            [],
            [Enumerated(minute: 10)]);

        Assert.Equal(LedgerReplay.LostTrack, ledger.InstanceAt["333333333333"]);
        Assert.Equal(1, ledger.Stats.WornEvicted);
        Assert.Equal(0, ledger.Stats.CarriedUsedUp);
    }

    [Fact]
    public void An_unnamed_item_on_the_player_whose_class_the_listing_leaves_out_is_no_longer_there()
    {
        var ledger = LedgerReplay.Run(
            [Move(Station, OnPlayer, "behr_rifle_ballistic_03_mag", amount: 2, minute: 2, moveType: "Interaction")],
            [],
            [new(new DateTimeOffset(2026, 7, 16, 12, 10, 0, TimeSpan.Zero), new HashSet<string>(), new HashSet<string> { "hdtc_undersuit_01_01_20" })]);

        Assert.Empty(ledger.Contents(OnPlayer));
        var (_, _, loose) = Assert.Single(ledger.Contents(LedgerReplay.LostTrack));
        Assert.Equal(2, loose!.Quantity);
        Assert.Equal(2, ledger.Stats.WornEvicted);
    }

    [Fact]
    public void A_new_item_on_a_body_port_replaces_the_old_one_even_at_the_same_instant()
    {
        var ledger = LedgerReplay.Run(
            [],
            [
                Worn(minute: 1, "784862306922", "qrt_utility_heavy_backpack_01_01_03") with { Port = "backpack" },
                Worn(minute: 1, "845736965831", "qrt_utility_heavy_backpack_01_01_03") with { Port = "backpack" },
            ]);

        Assert.Equal(LedgerReplay.LostTrack, ledger.InstanceAt["784862306922"]);
        Assert.Equal(OnPlayer, ledger.InstanceAt["845736965831"]);
    }

    [Fact]
    public void What_is_inside_a_backpack_the_body_no_longer_lists_goes_with_it()
    {
        var ledger = LedgerReplay.Run(
            [Move(Station, Backpack, "Drink_can_pips_01_t17_a", geid: "784099875932", minute: 3, moveType: "Store")],
            [Worn(minute: 1, Backpack.Key, "qrt_utility_heavy_backpack_01_01_03")],
            [Enumerated(minute: 1, Backpack.Key), Enumerated(minute: 10, "845736965831")]);

        Assert.Equal(LedgerReplay.LostTrack, ledger.InstanceAt[Backpack.Key]);
        Assert.Equal(Backpack, ledger.InstanceAt["784099875932"]);
    }

    [Fact]
    public void An_item_listed_again_stays_on_the_player_and_one_put_on_after_the_listing_is_not_contradicted()
    {
        var ledger = LedgerReplay.Run(
            [Move(Station, OnPlayer, "grin_utility_medium_helmet_01_01_01", geid: "726852737760", minute: 12, moveType: "Interaction")],
            [Worn(minute: 1, "845736965829", "hdtc_undersuit_01_01_20"), Worn(minute: 10, "845736965829", "hdtc_undersuit_01_01_20")],
            [Enumerated(minute: 1, "845736965829"), Enumerated(minute: 10, "845736965829")]);

        Assert.Equal(OnPlayer, ledger.InstanceAt["845736965829"]);
        Assert.Equal(OnPlayer, ledger.InstanceAt["726852737760"]);
        Assert.Equal(0, ledger.Stats.WornEvicted);
    }

    [Fact]
    public void An_evicted_item_sighted_again_without_a_move_is_counted()
    {
        // The reliability study reads this as an enumeration that left out something worn.
        var ledger = LedgerReplay.Run(
            [],
            [Worn(minute: 1, "845736965844", "qrt_utility_heavy_helmet_01_01_03"), Worn(minute: 20, "845736965844", "qrt_utility_heavy_helmet_01_01_03")],
            [Enumerated(minute: 10)]);

        Assert.Equal(1, ledger.Stats.EvictedThenResighted);
        Assert.Equal(OnPlayer, ledger.InstanceAt["845736965844"]);
    }

    [Fact]
    public void A_part_sits_in_its_parent_and_goes_where_the_parent_is_stored()
    {
        var ledger = LedgerReplay.Run(
            [Move(Nowhere, Station, "qrt_utility_heavy_helmet_01_01_03", geid: "845736965844", minute: 5, moveType: "Store")],
            [
                Worn(minute: 1, "845736965844", "qrt_utility_heavy_helmet_01_01_03"),
                Worn(minute: 1, "845736965845", "FP_Visor") with { Port = "helmet_visor", IsPart = true, ParentGeid = "845736965844" },
            ]);

        Assert.Equal(new Holding(InventoryKind.Container, "845736965844"), ledger.InstanceAt["845736965845"]);
        Assert.Equal(Station, ledger.InstanceAt["845736965844"]);
        Assert.True(ledger.IsPartParent("845736965844"));
    }

    [Fact]
    public void A_listed_item_loses_the_parts_the_listing_leaves_out()
    {
        var ledger = LedgerReplay.Run(
            [],
            [
                Worn(minute: 1, "845736965837", "behr_rifle_ballistic_03"),
                Worn(minute: 1, "845736965838", "behr_rifle_ballistic_03_mag") with { IsPart = true, ParentGeid = "845736965837" },
                Worn(minute: 10, "845736965837", "behr_rifle_ballistic_03"),
            ],
            [Enumerated(minute: 10, "845736965837")]);

        Assert.Equal(LedgerReplay.LostTrack, ledger.InstanceAt["845736965838"]);
        Assert.Equal(OnPlayer, ledger.InstanceAt["845736965837"]);
    }

    [Fact]
    public void Moves_the_server_dropped_leave_everything_at_the_source_the_player_saw_it_at()
    {
        // 2026-09-25 15:44-15:50: the Aril out and back, two Defiance out, all dropped. The
        // Aril's return starts in the backpack only because the client showed it there.
        var ledger = Run(
            Move(Station, Backpack, "grin_utility_medium_helmet_01_01_01", result: "lost", minute: 44),
            Move(Station, Backpack, "slaver_armor_heavy_helmet_01_9tails_01", result: "lost", minute: 46),
            Move(Station, Backpack, "slaver_armor_heavy_helmet_01_9tails_01", result: "lost", minute: 47),
            Move(Backpack, Station, "grin_utility_medium_helmet_01_01_01", result: "lost", minute: 50));

        Assert.Empty(ledger.Contents(Backpack));
        var station = ledger.Contents(Station).ToDictionary(c => c.ItemClass, c => c.Loose!.Quantity);
        Assert.Equal(1, station["grin_utility_medium_helmet_01_01_01"]);
        Assert.Equal(2, station["slaver_armor_heavy_helmet_01_9tails_01"]);
        Assert.Equal(4, ledger.Stats.LostSkipped);
        Assert.Equal(3, ledger.Stats.UnitsCreditedByLost);
    }

    [Fact]
    public void A_failed_move_ends_a_stall_as_surely_as_a_succeeded_one()
    {
        // Two separate stalls, one Defiance each, with a failed move between them: the second
        // must not count what the first seemed to move.
        var ledger = Run(
            Move(Station, Backpack, "slaver_armor_heavy_helmet_01_9tails_01", result: "lost", minute: 1),
            Move(Station, Crate, "rifle_01", result: "failed", minute: 2),
            Move(Station, Backpack, "slaver_armor_heavy_helmet_01_9tails_01", result: "lost", minute: 3));

        var (_, _, loose) = Assert.Single(ledger.Contents(Station));
        Assert.Equal(1, loose!.Quantity);
    }

    [Fact]
    public void A_named_move_the_server_dropped_leaves_a_new_item_at_its_source()
    {
        var ledger = Run(
            Move(Station, Crate, "rifle_01", geid: "111111111111", result: "lost", minute: 1));

        Assert.Equal(Station, ledger.InstanceAt["111111111111"]);
        Assert.Empty(ledger.Contents(Crate));
    }

    private static LedgerReplay.WornSighting Worn(int minute, string geid, string itemClass) =>
        new(new DateTimeOffset(2026, 7, 16, 12, minute, 0, TimeSpan.Zero), geid, itemClass);

    /// <summary>A game update the ledger moves the player's belongings through.</summary>
    private static LedgerReplay.Relocation Update(Holding spawn, int minute) =>
        new(new DateTimeOffset(2026, 7, 16, 12, minute, 0, TimeSpan.Zero), 12519617, spawn);

    private static LedgerReplay RunAcross(LedgerReplay.Relocation update, params MoveRecord[] moves) =>
        LedgerReplay.Run(moves, [], relocations: [update]);

    [Fact]
    public void A_game_update_moves_what_was_stored_or_equipped_to_where_the_player_spawned()
    {
        // Build 12519617: armour stored at Lorville and weapons last equipped were all taken
        // out of Area18, where the player first spawned after the update.
        var ledger = RunAcross(
            Update(OtherStation, minute: 5),
            Move(Nowhere, Station, "armor_core_01", geid: "111111111111", minute: 1),
            Move(Station, OnPlayer, "rifle_01", geid: "222222222222", minute: 2),
            Move(Nowhere, Station, "ammo_box_01", amount: 4, minute: 3));

        Assert.Equal(OtherStation, ledger.InstanceAt["111111111111"]);
        Assert.Equal(OtherStation, ledger.InstanceAt["222222222222"]);
        Assert.Equal(4, ledger.Contents(OtherStation).Single(c => c.ItemClass == "ammo_box_01").Loose!.Quantity);
        Assert.Empty(ledger.Contents(Station));
        Assert.All(ledger.NamedIn(OtherStation, "armor_core_01"), i => Assert.Equal(12519617, i.MovedByUpdate));
        Assert.Equal(6, ledger.Stats.RelocatedByUpdate);
    }

    [Fact]
    public void A_containers_contents_follow_it_through_an_update()
    {
        var ledger = RunAcross(
            Update(OtherStation, minute: 5),
            Move(Nowhere, Station, "crate_01", geid: Crate.Key, minute: 1),
            Move(Nowhere, Crate, "rifle_01", geid: "111111111111", minute: 2));

        Assert.Equal(OtherStation, ledger.InstanceAt[Crate.Key]);
        Assert.Equal(Crate, ledger.InstanceAt["111111111111"]);
    }

    [Fact]
    public void An_update_leaves_dropped_and_hand_carried_items_where_they_were()
    {
        // A drop despawns on its own, and a carry is most likely eaten or drunk: the next body
        // enumeration is what settles it, and it only looks on the player.
        var world = new Holding(InventoryKind.World, "");
        var ledger = RunAcross(
            Update(OtherStation, minute: 5),
            Move(Station, world, "rifle_01", geid: "111111111111", minute: 1, moveType: "Drop"),
            Carry(Crate, "drink_01", "222222222222", minute: 2));

        Assert.Equal(world, ledger.InstanceAt["111111111111"]);
        Assert.Equal(OnPlayer, ledger.InstanceAt["222222222222"]);
    }

    [Fact]
    public void A_move_out_of_the_spawn_station_after_an_update_confirms_the_relocation()
    {
        var ledger = RunAcross(
            Update(OtherStation, minute: 5),
            Move(Nowhere, Station, "armor_core_01", geid: "111111111111", minute: 1),
            Move(OtherStation, OnPlayer, "armor_core_01", geid: "111111111111", minute: 9));

        var check = Assert.Single(ledger.Stats.PlacementChecks);
        Assert.True(check.SamePlace);
        Assert.Equal(12519617, check.BelievedMovedByUpdate);
    }

    [Fact]
    public void A_class_level_move_after_an_update_finds_the_stack_at_the_spawn_station()
    {
        var ledger = RunAcross(
            Update(OtherStation, minute: 5),
            Move(Nowhere, Station, "ammo_box_01", amount: 4, minute: 1),
            Move(OtherStation, Crate, "ammo_box_01", amount: 3, minute: 9));

        Assert.Equal(3, ledger.Stats.UnitsFromLoose);
        Assert.DoesNotContain(ledger.Stats.Misses, m => m.Source == OtherStation);
    }

    // A04: an unnamed stack keeps how many of its units were inferred, so a named entity turning
    // up where it sits can absorb a unit that was a guess, never one the ledger counted. Each
    // theory pairs the case that exposes the defect with a control differing in one parameter.

    private const string Magazine = "behr_rifle_ballistic_03_mag";

    /// <summary>Where counted units are first credited, then taken from.</summary>
    private static readonly Holding SeedStation = new(InventoryKind.Location, "1000000001");

    /// <summary>Where named entities of the class sit before they turn up at the stack.</summary>
    private static readonly Holding ElsewhereStation = new(InventoryKind.Location, "1000000002");

    /// <summary>A real place the ledger never saw anything in: a class-level move out of it falls short.</summary>
    private static readonly Holding EmptyStation = new(InventoryKind.Location, "1000000003");

    private static readonly Holding TargetCrate = new(InventoryKind.Container, "681562156499");

    /// <summary>Units found at their source on the way in: counted, not inferred.</summary>
    private static IEnumerable<MoveRecord> Counted(Holding at, int units, int minute) =>
    [
        Move(Nowhere, SeedStation, Magazine, amount: units, minute: minute),
        Move(SeedStation, at, Magazine, amount: units, minute: minute),
    ];

    /// <summary>Units nobody could source: credited to the target as inferred.</summary>
    private static MoveRecord Unsourced(Holding at, int units, int minute) =>
        Move(EmptyStation, at, Magazine, amount: units, minute: minute);

    private static int LooseCount(LedgerReplay ledger, Holding at) =>
        ledger.Contents(at).Where(c => c.ItemClass == Magazine).Select(c => c.Loose?.Quantity ?? 0).SingleOrDefault();

    private static void AssertProvenanceOutcome(LedgerReplay ledger, Holding at, int named, int loose, int reconciled)
    {
        var actualNamed = ledger.NamedIn(at, Magazine).Count;
        var actualLoose = LooseCount(ledger, at);
        var actualReconciled = ledger.Stats.AnonymousReconciled;
        Assert.True(
            actualNamed == named && actualLoose == loose && actualReconciled == reconciled,
            $"Expected named={named}, loose={loose}, reconciled={reconciled}; got named={actualNamed}, " +
            $"loose={actualLoose}, reconciled={actualReconciled}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Named_arrivals_reconcile_no_more_units_of_a_mixed_stack_than_were_inferred(int namedArrivals)
    {
        // The audit's 10+1 probe, inverted: ten units counted at their source, one inferred, then
        // named entities already known elsewhere arrive. Only the inferred unit may be absorbed.
        var moves = new List<MoveRecord>
        {
            Move(Nowhere, SeedStation, Magazine, amount: 10, minute: 1),
            Move(SeedStation, TargetCrate, Magazine, amount: 10, minute: 2),
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 3),
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G2", minute: 4),
            Move(SeedStation, TargetCrate, Magazine, amount: 1, minute: 5),
        };
        for (var i = 1; i <= namedArrivals; i++)
        {
            moves.Add(Move(ElsewhereStation, TargetCrate, Magazine, geid: $"G{i}", minute: 5 + i));
        }

        AssertProvenanceOutcome(Run([.. moves]), TargetCrate, named: namedArrivals, loose: 10, reconciled: 1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Successive_reconciliations_never_spend_more_than_the_inferred_reserve(int namedArrivals)
    {
        var moves = new List<MoveRecord>();
        for (var i = 1; i <= namedArrivals; i++)
        {
            moves.Add(Move(Nowhere, ElsewhereStation, Magazine, geid: $"G{i}", minute: i));
        }

        moves.AddRange(Counted(TargetCrate, units: 3, minute: 10));
        moves.Add(Unsourced(TargetCrate, units: 2, minute: 12));
        for (var i = 1; i <= namedArrivals; i++)
        {
            moves.Add(Move(ElsewhereStation, TargetCrate, Magazine, geid: $"G{i}", minute: 20 + i));
        }

        AssertProvenanceOutcome(Run([.. moves]), TargetCrate, named: namedArrivals, loose: 3, reconciled: 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_partial_class_move_out_of_a_mixed_stack_keeps_its_inferred_unit_eligible_once(int namedArrivals)
    {
        // Which units a class-level move takes is never logged. The newest arrival leaves first,
        // as for named instances, so the older guess stays behind with six of the counted units.
        var moves = new List<MoveRecord>
        {
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 1),
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G2", minute: 2),
            Unsourced(TargetCrate, units: 1, minute: 3),
        };
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 4));
        moves.Add(Move(TargetCrate, OtherStation, Magazine, amount: 4, minute: 6));
        for (var i = 1; i <= namedArrivals; i++)
        {
            moves.Add(Move(ElsewhereStation, TargetCrate, Magazine, geid: $"G{i}", minute: 6 + i));
        }

        var ledger = Run([.. moves]);

        Assert.Equal(4, LooseCount(ledger, OtherStation));
        AssertProvenanceOutcome(ledger, TargetCrate, named: namedArrivals, loose: 6, reconciled: 1);
    }

    [Fact]
    public void A_complete_class_move_empties_a_mixed_stack_and_delivers_units_found_at_their_source()
    {
        var moves = new List<MoveRecord>
        {
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 1),
            Unsourced(TargetCrate, units: 1, minute: 2),
        };
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 3));
        moves.Add(Move(TargetCrate, Crate, Magazine, amount: 11, minute: 5));
        moves.Add(Move(ElsewhereStation, Crate, Magazine, geid: "G1", minute: 6));

        var ledger = Run([.. moves]);

        Assert.Empty(ledger.Contents(TargetCrate));
        var (_, named, loose) = Assert.Single(ledger.Contents(Crate));
        Assert.Equal("G1", Assert.Single(named).Geid);
        Assert.NotNull(loose);
        Assert.Equal(11, loose.Quantity);
        Assert.False(loose.Inferred);
        Assert.Equal(0, ledger.Stats.AnonymousReconciled);
        Assert.Equal(21, ledger.Stats.UnitsFromLoose);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Stacks_merged_by_an_update_keep_the_inferred_count_of_each_part(int namedArrivals)
    {
        // The update moves the station's three counted units and one guess onto the two counted
        // units already at the spawn station. The merged stack still holds a single guess.
        var moves = new List<MoveRecord>
        {
            Move(Nowhere, Crate, Magazine, geid: "G1", minute: 1),
            Move(Nowhere, Crate, Magazine, geid: "G2", minute: 2),
        };
        moves.AddRange(Counted(Station, units: 3, minute: 3));
        moves.AddRange(Counted(OtherStation, units: 2, minute: 4));
        moves.Add(Unsourced(Station, units: 1, minute: 6));
        for (var i = 1; i <= namedArrivals; i++)
        {
            moves.Add(Move(Crate, OtherStation, Magazine, geid: $"G{i}", minute: 30 + i));
        }

        var ledger = LedgerReplay.Run(moves, [], relocations: [Update(OtherStation, minute: 20)]);

        Assert.Empty(ledger.Contents(Station));
        Assert.Equal(4, ledger.Stats.RelocatedByUpdate);
        AssertProvenanceOutcome(ledger, OtherStation, named: namedArrivals, loose: 5, reconciled: 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Units_drawn_from_a_mixed_stack_on_the_player_leave_its_inferred_count_intact(int namedArrivals)
    {
        var moves = new List<MoveRecord>
        {
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 1),
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G2", minute: 2),
            Unsourced(OnPlayer, units: 1, minute: 3),
        };
        moves.AddRange(Counted(OnPlayer, units: 3, minute: 4));

        // The empty station cannot supply these two, so they are drawn from what the player carries.
        moves.Add(Unsourced(Crate, units: 2, minute: 6));
        for (var i = 1; i <= namedArrivals; i++)
        {
            moves.Add(Move(ElsewhereStation, OnPlayer, Magazine, geid: $"G{i}", minute: 6 + i));
        }

        var ledger = Run([.. moves]);

        Assert.Equal(2, ledger.Stats.UnitsFromCarried);
        Assert.Equal(2, LooseCount(ledger, Crate));
        AssertProvenanceOutcome(ledger, OnPlayer, named: namedArrivals, loose: 1, reconciled: 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Entities_drawn_from_lost_track_into_a_mixed_stack_absorb_only_its_inferred_unit(int drawn)
    {
        var moves = new List<MoveRecord>();
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 1));
        moves.Add(Move(SeedStation, TargetCrate, Magazine, amount: 1, minute: 3));
        moves.Add(Move(Station, OnPlayer, Magazine, geid: "L1", minute: 4, moveType: "Interaction"));
        moves.Add(Move(Station, OnPlayer, Magazine, geid: "L2", minute: 5, moveType: "Interaction"));
        moves.Add(Unsourced(TargetCrate, units: drawn, minute: 20));

        var ledger = LedgerReplay.Run(moves, [], [Enumerated(minute: 10)]);

        Assert.Equal(drawn, ledger.Stats.UnitsFromLostTrack);
        AssertProvenanceOutcome(ledger, TargetCrate, named: drawn, loose: 10, reconciled: 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Named_entities_moved_by_class_into_a_mixed_stack_absorb_only_its_inferred_unit(int drawn)
    {
        var moves = new List<MoveRecord>
        {
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 1),
            Move(Nowhere, ElsewhereStation, Magazine, geid: "G2", minute: 2),
        };
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 3));
        moves.Add(Unsourced(TargetCrate, units: 1, minute: 5));
        moves.Add(Move(ElsewhereStation, TargetCrate, Magazine, amount: drawn, minute: 6));

        var ledger = Run([.. moves]);

        Assert.Equal(drawn, ledger.Stats.UnitsFromNamed);
        AssertProvenanceOutcome(ledger, TargetCrate, named: drawn, loose: 10, reconciled: 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_stack_is_flagged_inferred_only_while_an_inferred_unit_remains_in_it(int inferredUnits)
    {
        var moves = new List<MoveRecord> { Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 1) };
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 2));
        moves.Add(Unsourced(TargetCrate, units: inferredUnits, minute: 4));
        moves.Add(Move(ElsewhereStation, TargetCrate, Magazine, geid: "G1", minute: 5));

        var ledger = Run([.. moves]);

        var (_, _, loose) = Assert.Single(ledger.Contents(TargetCrate));
        Assert.NotNull(loose);
        Assert.Equal(10 + inferredUnits - 1, loose.Quantity);
        var stillGuessed = inferredUnits > 1;
        Assert.True(
            loose.Inferred == stillGuessed && loose.DuplicateRisk == stillGuessed,
            $"Expected inferred={stillGuessed}, duplicateRisk={stillGuessed}; got inferred={loose.Inferred}, " +
            $"duplicateRisk={loose.DuplicateRisk}");
    }

    // The provenance groups themselves, as LooseIn exposes them.

    private static string GroupsOf(LedgerReplay ledger, Holding at) =>
        string.Join(" | ", ledger.LooseIn(at, Magazine).Select(g =>
            $"x{g.Quantity} confirmed={g.Confirmed} inferred={g.Inferred} risk={g.DuplicateRisk} update={g.MovedByUpdate}"));

    private static void AssertGroupsAddUp(LedgerReplay ledger, Holding at)
    {
        var groups = ledger.LooseIn(at, Magazine);
        Assert.All(groups, g => Assert.True(g.Quantity > 0, GroupsOf(ledger, at)));
        Assert.Equal(LooseCount(ledger, at), groups.Sum(g => g.Quantity));
    }

    [Fact]
    public void Each_provenance_group_of_a_stack_keeps_its_own_count_and_flags()
    {
        var moves = new List<MoveRecord> { Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 1) };
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 2));
        moves.Add(Unsourced(TargetCrate, units: 1, minute: 4));
        moves.Add(Move(Nowhere, SeedStation, Magazine, minute: 5));
        moves.Add(Move(SeedStation, TargetCrate, Magazine, result: null, minute: 5));

        var ledger = Run([.. moves]);

        Assert.Equal(
            "x10 confirmed=True inferred=False risk=False update= | x1 confirmed=True inferred=True risk=True update= | " +
            "x1 confirmed=False inferred=False risk=False update=",
            GroupsOf(ledger, TargetCrate));
        AssertGroupsAddUp(ledger, TargetCrate);
        var (_, _, loose) = Assert.Single(ledger.Contents(TargetCrate));
        Assert.NotNull(loose);
        Assert.False(loose.Confirmed);
        Assert.True(loose.Inferred);
        Assert.True(loose.DuplicateRisk);
    }

    [Fact]
    public void Partial_then_complete_moves_take_the_newest_units_and_leave_the_rest_of_each_group()
    {
        var moves = new List<MoveRecord> { Unsourced(TargetCrate, units: 1, minute: 3) };
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 4));
        moves.Add(Move(TargetCrate, OtherStation, Magazine, amount: 4, minute: 6));
        var partial = Run([.. moves]);

        Assert.Equal(
            "x1 confirmed=True inferred=True risk=False update= | x6 confirmed=True inferred=False risk=False update=",
            GroupsOf(partial, TargetCrate));
        AssertGroupsAddUp(partial, TargetCrate);

        moves.Add(Move(TargetCrate, OtherStation, Magazine, amount: 7, minute: 7));
        var complete = Run([.. moves]);

        Assert.Empty(complete.LooseIn(TargetCrate, Magazine));
        Assert.Empty(complete.Contents(TargetCrate));
        Assert.Equal("x11 confirmed=True inferred=False risk=False update=", GroupsOf(complete, OtherStation));
    }

    [Fact]
    public void An_update_merge_keeps_each_groups_flags_and_build()
    {
        var moves = new List<MoveRecord> { Move(Nowhere, Crate, Magazine, geid: "G1", minute: 1) };
        moves.AddRange(Counted(Station, units: 3, minute: 3));
        moves.AddRange(Counted(OtherStation, units: 2, minute: 4));
        moves.Add(Unsourced(Station, units: 1, minute: 6));
        var update = Update(OtherStation, minute: 20);

        var merged = LedgerReplay.Run(moves, [], relocations: [update]);

        Assert.Equal(
            "x2 confirmed=True inferred=False risk=False update= | x3 confirmed=True inferred=False risk=False update=12519617 | " +
            "x1 confirmed=True inferred=True risk=True update=12519617",
            GroupsOf(merged, OtherStation));
        AssertGroupsAddUp(merged, OtherStation);
        Assert.Equal(12519617, merged.Contents(OtherStation).Single().Loose!.MovedByUpdate);

        moves.Add(Move(Crate, OtherStation, Magazine, geid: "G1", minute: 31));
        var reconciled = LedgerReplay.Run(moves, [], relocations: [update]);

        Assert.Equal(
            "x2 confirmed=True inferred=False risk=False update= | x3 confirmed=True inferred=False risk=False update=12519617",
            GroupsOf(reconciled, OtherStation));
    }

    [Fact]
    public void A_stack_the_listing_leaves_out_keeps_its_groups_where_the_ledger_lost_track_of_it()
    {
        var moves = new List<MoveRecord> { Unsourced(OnPlayer, units: 1, minute: 1) };
        moves.AddRange(Counted(OnPlayer, units: 2, minute: 2));

        var ledger = LedgerReplay.Run(moves, [], [Enumerated(minute: 10)]);

        Assert.Empty(ledger.LooseIn(OnPlayer, Magazine));
        Assert.Equal(
            "x1 confirmed=True inferred=True risk=False update= | x2 confirmed=True inferred=False risk=False update=",
            GroupsOf(ledger, LedgerReplay.LostTrack));
        Assert.Equal(3, ledger.Stats.WornEvicted);
    }

    [Fact]
    public void A_first_sighting_absorbs_the_newest_unit_and_leaves_an_older_guess_for_a_later_arrival()
    {
        // An entity first seen arriving where unnamed units of its class sit is taken to be the
        // one most recently dragged in; the older guess stays, for one entity to absorb later.
        var moves = new List<MoveRecord>
        {
            Move(Nowhere, ElsewhereStation, Magazine, geid: "KNOWN", minute: 1),
            Unsourced(TargetCrate, units: 1, minute: 2),
        };
        moves.AddRange(Counted(TargetCrate, units: 1, minute: 3));
        moves.Add(Move(Nowhere, TargetCrate, Magazine, geid: "FIRST-SEEN", minute: 4));

        var firstSighting = Run([.. moves]);

        Assert.Equal("x1 confirmed=True inferred=True risk=True update=", GroupsOf(firstSighting, TargetCrate));

        moves.Add(Move(ElsewhereStation, TargetCrate, Magazine, geid: "KNOWN", minute: 5));
        var ledger = Run([.. moves]);

        Assert.Empty(ledger.LooseIn(TargetCrate, Magazine));
        Assert.Equal(2, ledger.Stats.AnonymousReconciled);
        Assert.Equal(2, ledger.NamedIn(TargetCrate, Magazine).Count);
    }

    // L05-R5: the groups LooseIn hands out are a read-only view. A caller that casts them back to
    // a writable collection must not be able to change what the ledger holds.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_caller_cannot_change_the_ledger_through_the_groups_LooseIn_hands_out(bool attemptMutation)
    {
        var moves = new List<MoveRecord> { Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 1) };
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 2));
        moves.Add(Unsourced(TargetCrate, units: 1, minute: 4));
        var ledger = Run([.. moves]);
        var before = GroupsOf(ledger, TargetCrate);

        if (attemptMutation
            && ledger.LooseIn(TargetCrate, Magazine) is ICollection<LedgerReplay.Anonymous> { IsReadOnly: false } writable)
        {
            writable.Clear();
        }

        // L05-R8: on the delivered read-only view the guard above is false, so nothing was tried.
        // Try a change through IList<T> regardless, and require it to be refused.
        if (attemptMutation)
        {
            var view = (IList<LedgerReplay.Anonymous>)ledger.LooseIn(TargetCrate, Magazine);
            Assert.Throws<NotSupportedException>(() => view.Clear());
        }

        Assert.Equal("x10 confirmed=True inferred=False risk=False update= | x1 confirmed=True inferred=True risk=True update=", before);
        Assert.Equal(before, GroupsOf(ledger, TargetCrate));
        Assert.Equal(11, LooseCount(ledger, TargetCrate));
    }

    // L05-R8 (iteration 03): every change a caller could try through IList<T> on the groups
    // LooseIn hands out is refused, and the ledger keeps both groups. "none" is the control: the
    // same view, nothing tried. Against the live list LooseIn handed out before iteration 02,
    // every change is accepted.

    [Theory]
    [InlineData("none")]
    [InlineData("Clear")]
    [InlineData("Add")]
    [InlineData("Insert")]
    [InlineData("RemoveAt")]
    [InlineData("Remove")]
    [InlineData("SetItem")]
    public void Every_change_tried_through_the_groups_LooseIn_hands_out_is_refused(string change)
    {
        var moves = new List<MoveRecord> { Move(Nowhere, ElsewhereStation, Magazine, geid: "G1", minute: 1) };
        moves.AddRange(Counted(TargetCrate, units: 10, minute: 2));
        moves.Add(Unsourced(TargetCrate, units: 1, minute: 4));
        var ledger = Run([.. moves]);
        var before = GroupsOf(ledger, TargetCrate);

        var view = (IList<LedgerReplay.Anonymous>)ledger.LooseIn(TargetCrate, Magazine);
        var counted = view[0];
        Action? attempt = change switch
        {
            "none" => null,
            "Clear" => () => view.Clear(),
            "Add" => () => view.Add(counted),
            "Insert" => () => view.Insert(0, counted),
            "RemoveAt" => () => view.RemoveAt(0),
            "Remove" => () => { view.Remove(counted); },
            "SetItem" => () => { view[0] = counted with { Quantity = 999 }; },
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "no such change"),
        };

        if (attempt is not null)
        {
            Assert.Throws<NotSupportedException>(attempt);
        }

        Assert.Equal("x10 confirmed=True inferred=False risk=False update= | x1 confirmed=True inferred=True risk=True update=", before);
        Assert.Equal(before, GroupsOf(ledger, TargetCrate));
        Assert.Equal(11, LooseCount(ledger, TargetCrate));
        Assert.Equal(11, ledger.LooseIn(TargetCrate, Magazine).Sum(g => g.Quantity));
    }

    // L05-R6: characterisation of behaviour already delivered by L05 iteration 01. These tests
    // were written after it and pass on it; they pin it so that a change has to be deliberate.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Groups_dated_alike_give_up_a_unit_from_the_one_created_last(bool guessCreatedLast)
    {
        // Both arrivals at 12:02; only their order in the log differs. TakeOne picks the newest
        // group by date, and on a tie the one created last.
        var moves = new List<MoveRecord>();
        if (guessCreatedLast)
        {
            moves.AddRange(Counted(TargetCrate, units: 2, minute: 2));
            moves.Add(Unsourced(TargetCrate, units: 1, minute: 2));
        }
        else
        {
            moves.Add(Unsourced(TargetCrate, units: 1, minute: 2));
            moves.AddRange(Counted(TargetCrate, units: 2, minute: 2));
        }

        moves.Add(Move(TargetCrate, OtherStation, Magazine, amount: 1, minute: 3));
        var ledger = Run([.. moves]);

        Assert.Equal(
            guessCreatedLast
                ? "x2 confirmed=True inferred=False risk=False update="
                : "x1 confirmed=True inferred=True risk=False update= | x1 confirmed=True inferred=False risk=False update=",
            GroupsOf(ledger, TargetCrate));
        AssertGroupsAddUp(ledger, TargetCrate);
        Assert.Equal("x1 confirmed=True inferred=False risk=False update=", GroupsOf(ledger, OtherStation));
    }

    private static void AssertStackHeader(LedgerReplay ledger, Holding at, int minute, string arrivedBy, int? movedByUpdate)
    {
        var (_, _, loose) = Assert.Single(ledger.Contents(at), c => c.ItemClass == Magazine);
        Assert.NotNull(loose);
        Assert.Equal(
            (new DateTimeOffset(2026, 7, 16, 12, minute, 0, TimeSpan.Zero), arrivedBy, movedByUpdate),
            (loose.Since, loose.ArrivedBy, loose.MovedByUpdate));
    }

    [Fact]
    public void The_stack_header_follows_arrivals_and_update_merges_but_not_a_withdrawn_group()
    {
        // The header is the whole-stack date, arrival and update that a listing and an update
        // decide on. An arrival re-dates it; an update merge keeps the newer of the two stacks'
        // and stamps the build; a withdrawal leaves it as it was, even when the group that set
        // it is gone (inherited from before L05: review objection L05-R2, registered as D03).
        var moves = new List<MoveRecord>();
        moves.AddRange(Counted(Station, units: 3, minute: 3));
        moves.AddRange(Counted(OtherStation, units: 2, minute: 4));
        moves.Add(Move(EmptyStation, Station, Magazine, amount: 1, minute: 6, moveType: "Store"));

        var added = Run([.. moves]);
        AssertStackHeader(added, Station, minute: 6, arrivedBy: "Store", movedByUpdate: null);
        AssertStackHeader(added, OtherStation, minute: 4, arrivedBy: "Move", movedByUpdate: null);

        var update = Update(OtherStation, minute: 20);
        var merged = LedgerReplay.Run(moves, [], relocations: [update]);
        AssertStackHeader(merged, OtherStation, minute: 6, arrivedBy: "Store", movedByUpdate: 12519617);

        moves.Add(Move(OtherStation, Crate, Magazine, amount: 1, minute: 30));
        var withdrawn = LedgerReplay.Run(moves, [], relocations: [update]);

        Assert.Equal(
            "x2 confirmed=True inferred=False risk=False update= | x3 confirmed=True inferred=False risk=False update=12519617",
            GroupsOf(withdrawn, OtherStation));
        AssertStackHeader(withdrawn, OtherStation, minute: 6, arrivedBy: "Store", movedByUpdate: 12519617);
    }

    /// <summary>
    /// Ten counted units Stored on the player, then one unconfirmed unit Carried in hand (a group
    /// of its own), then a one-unit class move out that withdraws the newest group: the carried one.
    /// </summary>
    private static List<MoveRecord> StoredStackWhoseCarriedUnitLeft() =>
    [
        Move(Nowhere, SeedStation, Magazine, amount: 10, minute: 1),
        Move(SeedStation, OnPlayer, Magazine, amount: 10, minute: 2, moveType: "Store"),
        Move(Nowhere, SeedStation, Magazine, amount: 1, minute: 3),
        Move(SeedStation, OnPlayer, Magazine, amount: 1, result: null, minute: 4, moveType: "Interaction") with { Action = "Carry" },
        Move(OnPlayer, OtherStation, Magazine, amount: 1, minute: 5),
    ];

    [Fact]
    public void A_listing_still_uses_up_a_stored_stack_whose_carried_unit_has_left()
    {
        // Astra's S1 probe (revue-01). Inherited behaviour, pinned as it is, not as it should be:
        // the header still says Carry, so a listing that omits the class deletes the ten Stored
        // units as used up instead of moving them to LostTrack (L05-R2, registered as D03).
        var moves = StoredStackWhoseCarriedUnitLeft();

        // GroupsOf is this file's own one-line account of every group (count and all four flags),
        // compared whole so that a change to any of them shows. It is not a production text. The
        // structured twin, A_listing_still_uses_up_the_stored_units_counted_one_by_one (L05-R10),
        // checks the same quantities and flags field by field, with the replay's counters.
        var beforeListing = Run([.. moves]);
        Assert.Equal("x10 confirmed=True inferred=False risk=False update=", GroupsOf(beforeListing, OnPlayer));
        AssertStackHeader(beforeListing, OnPlayer, minute: 4, arrivedBy: "Carry", movedByUpdate: null);

        var ledger = LedgerReplay.Run(moves, [], [Enumerated(minute: 10)]);

        Assert.Empty(ledger.LooseIn(OnPlayer, Magazine));
        Assert.Empty(ledger.LooseIn(LedgerReplay.LostTrack, Magazine));
        Assert.Equal(10, ledger.Stats.CarriedUsedUp);
        Assert.Equal(0, ledger.Stats.WornEvicted);
        Assert.Equal("x1 confirmed=True inferred=False risk=False update=", GroupsOf(ledger, OtherStation));
    }

    [Fact]
    public void An_update_still_leaves_behind_a_stored_stack_whose_carried_unit_has_left()
    {
        // Astra's S2 probe (revue-01). Inherited behaviour, pinned as it is: the Carry header
        // keeps the ten Stored units on the player through the update, while the unit that left
        // is moved with the station it went to (L05-R2, registered as D03).
        var ledger = LedgerReplay.Run(StoredStackWhoseCarriedUnitLeft(), [], relocations: [Update(Station, minute: 20)]);

        // GroupsOf strings are compared whole, as in S1 above; the structured twin is
        // An_update_still_leaves_the_stored_units_on_the_player_counted_one_by_one (L05-R10).
        Assert.Equal("x10 confirmed=True inferred=False risk=False update=", GroupsOf(ledger, OnPlayer));
        AssertStackHeader(ledger, OnPlayer, minute: 4, arrivedBy: "Carry", movedByUpdate: null);
        Assert.Equal("x1 confirmed=True inferred=False risk=False update=12519617", GroupsOf(ledger, Station));
        Assert.Empty(ledger.LooseIn(OtherStation, Magazine));
        Assert.Equal(1, ledger.Stats.RelocatedByUpdate);
    }

    // L05-R10 (iteration 03): structured twins of S1 and S2. They pin the same inherited
    // behaviour (D03: the Carry header outlives the carried unit), with no compared string: the
    // replay's counters, and each group's quantity and flags as fields. The control (the carried
    // unit never leaves) differs in one parameter: there the Carry header is accurate, so a fix
    // of D03 must leave it as it is, while the case where the unit left must change.

    private static void AssertGroup(
        LedgerReplay.Anonymous group, int quantity, bool confirmed, bool inferred, int? movedByUpdate)
    {
        Assert.Equal(
            (quantity, confirmed, inferred, false, movedByUpdate),
            (group.Quantity, group.Confirmed, group.Inferred, group.DuplicateRisk, group.MovedByUpdate));
    }

    private static List<MoveRecord> StoredStackWithCarriedUnit(bool carriedUnitLeft)
    {
        var moves = StoredStackWhoseCarriedUnitLeft();
        if (!carriedUnitLeft) moves.RemoveAt(moves.Count - 1);
        return moves;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_listing_still_uses_up_the_stored_units_counted_one_by_one(bool carriedUnitLeft)
    {
        var moves = StoredStackWithCarriedUnit(carriedUnitLeft);

        var beforeListing = Run([.. moves]);
        var held = beforeListing.LooseIn(OnPlayer, Magazine);
        Assert.Equal(carriedUnitLeft ? 1 : 2, held.Count);
        AssertGroup(held[0], quantity: 10, confirmed: true, inferred: false, movedByUpdate: null);
        if (!carriedUnitLeft) AssertGroup(held[1], quantity: 1, confirmed: false, inferred: false, movedByUpdate: null);
        Assert.Equal("Carry", beforeListing.Contents(OnPlayer).Single().Loose!.ArrivedBy);

        // L05-R15 (iteration 04): the header's date and update as well, the two other fields
        // AssertStackHeader checks in S1, so that a fix of D03 changing only one of them shows here.
        var header = beforeListing.Contents(OnPlayer).Single().Loose!;
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 12, 4, 0, TimeSpan.Zero), header.Since);
        Assert.Null(header.MovedByUpdate);

        var ledger = LedgerReplay.Run(moves, [], [Enumerated(minute: 10)]);

        Assert.Equal(carriedUnitLeft ? 10 : 11, ledger.Stats.CarriedUsedUp);
        Assert.Equal(0, ledger.Stats.WornEvicted);
        Assert.Equal(0, ledger.Stats.RelocatedByUpdate);
        Assert.Empty(ledger.Contents(OnPlayer));
        Assert.Empty(ledger.LooseIn(LedgerReplay.LostTrack, Magazine));

        var left = ledger.LooseIn(OtherStation, Magazine);
        Assert.Equal(carriedUnitLeft ? 1 : 0, left.Count);
        if (carriedUnitLeft) AssertGroup(left[0], quantity: 1, confirmed: true, inferred: false, movedByUpdate: null);

        // Every one of the eleven units is accounted for: used up, or at the station it went to.
        Assert.Equal(11, ledger.Stats.CarriedUsedUp + left.Sum(g => g.Quantity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_update_still_leaves_the_stored_units_on_the_player_counted_one_by_one(bool carriedUnitLeft)
    {
        var ledger = LedgerReplay.Run(
            StoredStackWithCarriedUnit(carriedUnitLeft), [], relocations: [Update(Station, minute: 20)]);

        var held = ledger.LooseIn(OnPlayer, Magazine);
        Assert.Equal(carriedUnitLeft ? 1 : 2, held.Count);
        AssertGroup(held[0], quantity: 10, confirmed: true, inferred: false, movedByUpdate: null);
        if (!carriedUnitLeft) AssertGroup(held[1], quantity: 1, confirmed: false, inferred: false, movedByUpdate: null);
        Assert.Equal("Carry", ledger.Contents(OnPlayer).Single().Loose!.ArrivedBy);

        // L05-R15 (iteration 04): the header's date and update as well, as AssertStackHeader checks
        // them in S2: the update left the stack behind and did not stamp it.
        var header = ledger.Contents(OnPlayer).Single().Loose!;
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 12, 4, 0, TimeSpan.Zero), header.Since);
        Assert.Null(header.MovedByUpdate);

        Assert.Equal(carriedUnitLeft ? 1 : 0, ledger.Stats.RelocatedByUpdate);
        Assert.Equal(0, ledger.Stats.CarriedUsedUp);
        Assert.Equal(0, ledger.Stats.WornEvicted);
        Assert.Empty(ledger.LooseIn(OtherStation, Magazine));

        var atSpawn = ledger.LooseIn(Station, Magazine);
        Assert.Equal(carriedUnitLeft ? 1 : 0, atSpawn.Count);
        if (carriedUnitLeft) AssertGroup(atSpawn[0], quantity: 1, confirmed: true, inferred: false, movedByUpdate: 12519617);

        Assert.Equal(11, held.Sum(g => g.Quantity) + atSpawn.Sum(g => g.Quantity));
    }
}
