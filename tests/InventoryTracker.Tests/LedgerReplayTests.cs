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
}
