using InventoryTracker.Model;

namespace InventoryTracker.Tests.Playtest;

/// <summary>
/// The drop test of 2026-10-12 in a hangar at Keeger, each step checked in game by the player:
/// two items dropped, a logout and a switch to an EU server, then both picked up again — the
/// hangar had kept them. See the fixture's README.
/// </summary>
public sealed class DropPickupTests
{
    private const string Day = "2026-10-12";

    /// <summary>Keeger's location id. The hangar shares it with the rest of the station.</summary>
    private const string Keeger = "3058615591";

    private const string Knife = "268727137002";
    private const string Arclight = "862186749926";

    [Fact]
    public void A_drop_out_of_the_station_inventory_lands_at_that_station()
    {
        // 17:55:03 request 12 dropped the knife out of Keeger's inventory.
        using var replay = PlaytestReplay.On(Day, "17:55:10");

        var knife = replay.Named(Knife);
        Assert.NotNull(knife);
        Assert.True(knife.IsDropped);
        Assert.Equal(Keeger, knife.Root!.Key);
    }

    [Fact]
    public void A_drop_out_of_the_backpack_lands_where_the_player_stands()
    {
        // 17:55:29 request 13 dropped the Arclight out of the worn backpack. The line names the
        // backpack, not a place; the player had been at Keeger since 17:53:27.
        using var replay = PlaytestReplay.On(Day, "17:55:40");

        var arclight = replay.Named(Arclight);
        Assert.NotNull(arclight);
        Assert.True(arclight.IsDropped);
        Assert.Equal(Keeger, arclight.Root!.Key);
    }

    [Fact]
    public void A_logout_and_a_server_switch_leave_dropped_items_where_they_fell()
    {
        // 17:56:02 the player logged out, and was back at Keeger on an EU server at 17:57:04.
        // Nothing says the floor was cleared, and the hangar had in fact kept both.
        using var replay = PlaytestReplay.On(Day, "17:57:10");

        Assert.Equal(Keeger, replay.RootOf(Knife)?.Key);
        Assert.Equal(Keeger, replay.RootOf(Arclight)?.Key);
        Assert.True(replay.Named(Knife)?.IsDropped);
        Assert.True(replay.Named(Arclight)?.IsDropped);
    }

    [Fact]
    public void Picking_up_into_the_hand_takes_the_item_off_the_floor()
    {
        // 17:57:18 the Arclight was picked up into the right hand. No move line says so, only
        // the attachment of the same geid to the hand.
        using var replay = PlaytestReplay.On(Day, "17:57:20");

        Assert.Equal(InventoryKind.Equipped, replay.RootOf(Arclight)?.Kind);
        Assert.True(replay.Named(Knife)?.IsDropped);
    }

    [Fact]
    public void A_picked_up_item_is_carried_in_the_backpack_or_worn_and_nothing_is_left_on_the_floor()
    {
        // 17:57:23 the knife went to the hand, then the game holstered it on utility_attach_2;
        // 17:57:28 request 24 stored the Arclight in the backpack, naming the same geid.
        using var replay = PlaytestReplay.On(Day, "17:58:00");

        Assert.Equal("equipped on your character", replay.Named(Knife)?.Where);
        Assert.Equal("carried on your character", replay.Named(Arclight)?.Where);
        Assert.DoesNotContain(replay.Holdings, h => h.IsDropped);
    }
}
