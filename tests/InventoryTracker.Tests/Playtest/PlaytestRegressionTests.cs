using InventoryTracker.Model;
using InventoryTracker.Resolve;

namespace InventoryTracker.Tests.Playtest;

/// <summary>
/// What the tracker already got right during the 2026-09-25 playtest, each step checked in
/// game by the player. These hold the behaviour in place while the defects found in the same
/// session are fixed around it.
/// </summary>
public sealed class PlaytestRegressionTests
{
    private const string QrtHelmet = "845736965844";
    private const string QrtBackpack = "845736965831";
    private const string ArilHelmet = "726852737760";
    private const string Defiance = "687179504351";
    private const string BehrSniper = "845736965832";
    private const string BehrRifle = "845736965837";
    private const string A03Sniper = "735739878129";
    private const string RsiBackpack = "723713568285";

    /// <summary>The login placeholder set: every one of its geids starts with 2000.</summary>
    private static bool IsPlaceholder(string geid) => geid.StartsWith("2000", StringComparison.Ordinal);

    [Fact]
    public void A_server_switch_re_sends_the_same_loadout_and_adds_nothing()
    {
        // 15:53:23 the player left for another server. The new login re-sent the loadout
        // under the same geids, and an identical geid is the same entity.
        using var before = PlaytestReplay.Until("15:53:00");
        using var after = PlaytestReplay.Until("15:57:00");

        var real = (PlaytestReplay r) => r.OnThePlayer().Where(g => !IsPlaceholder(g)).ToHashSet();

        Assert.NotEmpty(real(before));
        Assert.Equal(real(before).Order(), real(after).Order());
    }

    [Fact]
    public void A_helmet_swapped_at_a_station_is_stored_there_and_the_new_one_is_worn()
    {
        // 15:57:37 request 53 stored the worn helmet, request 54 equipped the Aril in its place.
        using var replay = PlaytestReplay.Until("15:58:00");

        AssertAt(replay, QrtHelmet, PlaytestReplay.Area18, Confidence.High);
        Assert.Contains(ArilHelmet, replay.OnThePlayer());
    }

    [Fact]
    public void A_helmet_stored_in_the_worn_backpack_is_carried()
    {
        // 15:59:19 request 55 stored the Aril from the head into the backpack.
        using var replay = PlaytestReplay.Until("15:59:30");

        var aril = replay.Named(ArilHelmet);
        Assert.NotNull(aril);
        Assert.Equal("carried on your character", aril.Where);
        Assert.Equal(Confidence.High, aril.Confidence);
    }

    [Fact]
    public void A_drop_is_in_the_world_until_it_is_picked_up_into_the_station()
    {
        // 16:00:12 request 56 dropped a Defiance helmet from Area18's inventory onto the floor;
        // 16:01:33 request 57 stored it straight back, naming the same geid.
        using (var dropped = PlaytestReplay.Until("16:00:30"))
        {
            Assert.Equal(InventoryKind.World, dropped.RootOf(Defiance)?.Kind);
        }

        using var stored = PlaytestReplay.Until("16:02:00");
        AssertAt(stored, Defiance, PlaytestReplay.Area18, Confidence.High);
    }

    [Fact]
    public void A_weapon_swap_stores_the_old_rifle_and_equips_the_new_one()
    {
        // 20:00:05 request 11 stored the behr sniper, request 12 equipped the A03 from Area18.
        using var replay = PlaytestReplay.Until("20:01:00");

        AssertAt(replay, BehrSniper, PlaytestReplay.Area18, Confidence.High);
        Assert.Contains(A03Sniper, replay.OnThePlayer());
    }

    [Fact]
    public void A_backpack_swap_stores_the_guns_and_the_backpack_with_its_contents()
    {
        // 20:02:35 requests 13-15 stored both guns, then the backpack holding the Aril; request
        // 16 equipped the rsi backpack from Area18. The player confirmed all of it in game.
        using var replay = PlaytestReplay.Until("20:03:00");

        AssertAt(replay, A03Sniper, PlaytestReplay.Area18, Confidence.High);
        AssertAt(replay, BehrRifle, PlaytestReplay.Area18, Confidence.High);
        AssertAt(replay, QrtBackpack, PlaytestReplay.Area18, Confidence.High);

        // The Aril was never moved itself: it follows the backpack it is in.
        AssertAt(replay, ArilHelmet, PlaytestReplay.Area18, Confidence.High);

        Assert.Contains(RsiBackpack, replay.OnThePlayer());
    }

    [Fact]
    public void A_death_at_a_station_keeps_the_backpack_and_what_is_in_it()
    {
        // 20:08:34 the player died at Area18 and respawned in a medbed. The body burst at
        // 20:08:55 re-sent the same geids, and the player confirmed the backpack still held the
        // cds helmet moved into it at 20:06:49.
        using var before = PlaytestReplay.Until("20:08:30");
        using var after = PlaytestReplay.Until("20:09:05");

        foreach (var replay in new[] { before, after })
        {
            Assert.Contains(QrtBackpack, replay.OnThePlayer());
            Assert.Contains(QrtHelmet, replay.OnThePlayer());
            Assert.Equal(1, replay.UnitsAt(PlaytestReplay.Equipped, "cds_undersuit_helmet_01_01_01"));
        }
    }

    private static void AssertAt(PlaytestReplay replay, string geid, Holding place, Confidence confidence)
    {
        var row = replay.Named(geid);
        Assert.NotNull(row);
        Assert.Equal(place.Kind, row.Root?.Kind);
        Assert.Equal(place.Key, row.Root?.Key);
        Assert.Equal(confidence, row.Confidence);
    }
}
