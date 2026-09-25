using InventoryTracker.Model;

namespace InventoryTracker.Tests.Playtest;

/// <summary>
/// Moves the server never processed did not happen — checked against the stalled queue and
/// server switch of the 2026-09-25 playtest, which the player verified in game.
/// </summary>
public sealed class LostRequestTests
{
    private const string Aril = "grin_utility_medium_helmet_01_01_01";
    private const string Defiance = "slaver_armor_heavy_helmet_01_9tails_01";

    [Fact]
    public void While_the_connection_is_up_a_pending_move_still_shows_but_unconfirmed()
    {
        // 15:46 the two Defiance helmets were dragged into the backpack; the server's queue had
        // stalled at 15:44 and never processed them. The session is still live at 15:52.
        using var replay = PlaytestReplay.Until("15:52:00", live: true);

        var carried = replay.Holdings.Single(h => h.ItemClass == Defiance);
        Assert.Equal(2, carried.Quantity);
        Assert.Equal(InventoryKind.Equipped, carried.Root?.Kind);
        Assert.Contains("the game never confirmed the move that put it here", carried.Caveats);
    }

    [Fact]
    public void Moves_dropped_when_the_player_switched_servers_leave_the_helmets_where_they_were()
    {
        // 15:53:23 the connection closed. After the reconnect the player found the backpack
        // empty and the Aril and both Defiance helmets in Area18's inventory.
        using var replay = PlaytestReplay.Until("15:54:00", live: true);

        Assert.Equal("4", replay.Scalar("SELECT COUNT(*) FROM move WHERE result = 'lost'"));
        Assert.Equal("28,29,30,31", replay.Scalar(
            "SELECT group_concat(request_no) FROM (SELECT request_no FROM move WHERE result = 'lost' ORDER BY request_no)"));

        Assert.Equal(2, replay.UnitsAt(PlaytestReplay.Area18, Defiance));
        Assert.Equal(1, replay.UnitsAt(PlaytestReplay.Area18, Aril));
        Assert.Equal(0, replay.UnitsAt(PlaytestReplay.Equipped, Defiance));
        Assert.Equal(0, replay.UnitsAt(PlaytestReplay.Equipped, Aril));
    }

    [Fact]
    public void A_rotated_log_voids_what_was_still_pending_when_it_ended()
    {
        // The same file read as a finished backup, cut before the disconnect line.
        using var replay = PlaytestReplay.Until("15:52:00");

        Assert.Equal(2, replay.UnitsAt(PlaytestReplay.Area18, Defiance));
        Assert.Equal(0, replay.UnitsAt(PlaytestReplay.Equipped, Defiance));
    }

    [Fact]
    public void A_drop_from_the_station_takes_one_of_the_helmets_that_never_left_it()
    {
        // 16:00:12 one Defiance was dropped from Area18 and 16:01:33 stored back: two at
        // Area18, none anywhere else. Before this fix the drop invented a third.
        using var replay = PlaytestReplay.Until("16:02:00");

        Assert.Equal(2, replay.UnitsAt(PlaytestReplay.Area18, Defiance));
        Assert.Equal(2, replay.Holdings.Where(h => h.ItemClass == Defiance).Sum(h => h.Quantity));
    }

    [Fact]
    public void Only_the_four_dropped_requests_are_lost_all_day_and_no_store_companion_is()
    {
        using var replay = PlaytestReplay.Until("20:14:50");

        Assert.Equal("4", replay.Scalar("SELECT COUNT(*) FROM move WHERE result = 'lost'"));
        Assert.Equal("0", replay.Scalar("SELECT COUNT(*) FROM move WHERE move_type = 'StoreItem' AND result = 'lost'"));
        Assert.Equal(2, replay.UnitsAt(PlaytestReplay.Area18, Defiance));
    }
}
