namespace InventoryTracker.Tests.Playtest;

/// <summary>
/// Every attachment line is kept, grouped into the bursts the game writes them in, with each
/// sub-attachment tied to the item it hangs off — checked against the 2026-09-25 playtest.
/// </summary>
public sealed class AttachmentHistoryTests
{
    [Fact]
    public void The_login_placeholder_set_is_stored_but_never_worn()
    {
        // 15:35:06 the game attached its 23 stand-ins; the real loadout followed at 15:35:47.
        // The player owns no klwe pistol and was not wearing an odyssey undersuit.
        using var replay = PlaytestReplay.Until("15:36:00");

        Assert.Equal("23", replay.Scalar(
            "SELECT COUNT(*) FROM attachment_sighting WHERE placeholder = 1 AND ts BETWEEN '2026-09-25T15:35' AND '2026-09-25T15:35:10'"));

        Assert.DoesNotContain(replay.OnThePlayer(), g => g.StartsWith("2000", StringComparison.Ordinal));
        Assert.Equal(0, replay.UnitsAt(PlaytestReplay.Equipped, "klwe_pistol_energy_01"));
        Assert.Equal(0, replay.UnitsAt(PlaytestReplay.Equipped, "rsi_odyssey_undersuit_01_01_01"));
        Assert.Equal(1, replay.UnitsAt(PlaytestReplay.Equipped, "hdtc_undersuit_01_01_20"));
    }

    [Fact]
    public void A_login_burst_is_one_burst_and_the_placeholder_set_another()
    {
        using var replay = PlaytestReplay.Until("15:36:00");

        Assert.Equal("2", replay.Scalar(
            "SELECT COUNT(DISTINCT burst_id) FROM attachment_sighting WHERE ts >= '2026-09-25T15:35'"));
        Assert.Equal("35", replay.Scalar(
            "SELECT COUNT(*) FROM attachment_sighting WHERE ts >= '2026-09-25T15:35' AND placeholder = 0"));
    }

    [Fact]
    public void A_sub_attachment_is_tied_to_the_item_it_hangs_off()
    {
        using var replay = PlaytestReplay.Until("15:36:00");

        // The qrt helmet's visor, and both guns' magazines, optics, barrels and underbarrels.
        Assert.Equal("845736965844", ParentOf(replay, "845736965845"));
        foreach (var part in new[] { "845736965833", "845736965834", "845736965835", "845736965836" })
        {
            Assert.Equal("845736965832", ParentOf(replay, part));
        }

        foreach (var part in new[] { "845736965838", "845736965839", "845736965840", "845736965841" })
        {
            Assert.Equal("845736965837", ParentOf(replay, part));
        }

        // The guns hang off the backpack port, but are items of their own, not parts.
        Assert.Equal("", ParentOf(replay, "845736965832"));
    }

    [Fact]
    public void An_equip_mid_session_ties_the_new_helmets_parts_to_it_and_ignores_the_local_prediction()
    {
        // 15:57:37 the client predicted the equip under local geids 10721-10723; at 15:57:39
        // the server attached the Aril 726852737760 with its necksock and visor.
        using var replay = PlaytestReplay.Until("15:58:00");

        Assert.Equal("726852737760", ParentOf(replay, "781903179025"));
        Assert.Equal("726852737760", ParentOf(replay, "781903179026"));
        Assert.Equal("0", replay.Scalar(
            "SELECT COUNT(*) FROM attachment_sighting WHERE geid IN ('10721', '10722', '10723')"));
    }

    private static string? ParentOf(PlaytestReplay replay, string geid) => replay.Scalar(
        $"SELECT COALESCE(parent_geid, '') FROM attachment_sighting WHERE geid = '{geid}' ORDER BY ts DESC LIMIT 1");
}
