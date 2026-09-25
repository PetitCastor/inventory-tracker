using InventoryTracker.Model;
using InventoryTracker.Resolve;

namespace InventoryTracker.Tests.Playtest;

/// <summary>
/// Each listing of the player's body replaces what the ledger had on the player: checked
/// against the relogs, the death in space and the death at a station of the 2026-09-25
/// playtest.
/// </summary>
public sealed class WornEnumerationTests
{
    /// <summary>The loadout the player wore before dying in space, under its pre-death geids.</summary>
    private static readonly string[] BeforeDeath =
    [
        "784862306920", // hdtc undersuit
        "784862306921", // qrt core
        "784862306922", // qrt backpack
        "784862306923", // behr sniper
        "784862306928", // behr rifle
        "784862306933", // qrt arms
        "784862306934", // qrt legs
        "784862306935", // qrt helmet
    ];

    private static readonly string[] WornClasses =
    [
        "hdtc_undersuit_01_01_20",
        "qrt_utility_heavy_arms_01_01_03",
        "qrt_utility_heavy_core_01_01_03",
        "qrt_utility_heavy_legs_01_01_03",
        "qrt_utility_heavy_helmet_01_01_03",
        "qrt_utility_heavy_backpack_01_01_03",
    ];

    [Fact]
    public void The_loadout_re_issued_after_a_death_in_space_replaces_the_old_one()
    {
        // 10:54:23 the player died in the Mole; one second later the game attached the same
        // classes under new geids, with no body line: the old ones went to the corpse.
        using var replay = PlaytestReplay.Until("10:56:00");

        Assert.Empty(replay.OnThePlayer().Intersect(BeforeDeath));
        foreach (var itemClass in WornClasses.Append("behr_rifle_ballistic_03").Append("behr_sniper_ballistic_01"))
        {
            Assert.Equal(1, replay.UnitsAt(PlaytestReplay.Equipped, itemClass));
        }
    }

    [Fact]
    public void A_backpack_no_longer_worn_is_not_carried_and_says_its_whereabouts_are_unknown()
    {
        using var replay = PlaytestReplay.Until("10:56:00");

        var backpack = replay.Named("784862306922");
        Assert.NotNull(backpack);
        Assert.Equal(InventoryKind.LostTrack, backpack.Root?.Kind);
        Assert.Equal(Confidence.Low, backpack.Confidence);
        Assert.Contains(backpack.Caveats, c => c.StartsWith("last seen on your character 2026-09-25", StringComparison.Ordinal));
    }

    [Fact]
    public void At_the_end_of_the_day_each_piece_of_the_loadout_is_worn_once_and_the_guns_are_stored()
    {
        // The player's own account at 20:10: armour, undersuit and backpack on, no weapon.
        using var replay = PlaytestReplay.Until("20:14:50");

        foreach (var itemClass in WornClasses)
        {
            Assert.Equal(1, replay.UnitsAt(PlaytestReplay.Equipped, itemClass));
        }

        foreach (var gun in new[] { "behr_rifle_ballistic_03", "behr_sniper_ballistic_01", "gmni_sniper_ballistic_01" })
        {
            Assert.Equal(0, replay.UnitsAt(PlaytestReplay.Equipped, gun));
            Assert.Equal(1, replay.UnitsAt(PlaytestReplay.Area18, gun));
        }
    }

    [Fact]
    public void A_death_at_a_station_takes_nothing_worn_off_the_player()
    {
        // 20:08:55 the medbed respawn re-sent every worn entity under the same geid.
        using var before = PlaytestReplay.Until("20:08:30");
        using var after = PlaytestReplay.Until("20:09:05");

        var worn = (PlaytestReplay r) => r.Holdings
            .Where(h => h.Geid is not null && h.Root?.Kind == InventoryKind.Equipped && !IsWeaponPart(h.ItemClass))
            .Select(h => h.Geid!)
            .Order()
            .ToList();

        Assert.Equal(worn(before), worn(after));
    }

    [Fact]
    public void A_relog_evicts_nothing()
    {
        // 19:57 the player relaunched; the new login listed what they had logged out with. The
        // player confirmed in game: head empty, the Aril in the backpack, the qrt helmet at Area18.
        using var replay = PlaytestReplay.Until("19:59:00");

        Assert.Equal(0, replay.Resolver.ReplayStats.EvictedThenResighted);
        foreach (var itemClass in WornClasses.Where(c => c != "qrt_utility_heavy_helmet_01_01_03"))
        {
            Assert.Equal(1, replay.UnitsAt(PlaytestReplay.Equipped, itemClass));
        }

        Assert.Equal(1, replay.UnitsAt(PlaytestReplay.Area18, "qrt_utility_heavy_helmet_01_01_03"));
        Assert.Equal("carried on your character", replay.Named("726852737760")?.Where);
    }

    /// <summary>
    /// Parts of a stored weapon are still left on the player until they follow their weapon
    /// (task T3); they are not what this test is about.
    /// </summary>
    private static bool IsWeaponPart(string itemClass) =>
        itemClass.EndsWith("_mag", StringComparison.Ordinal)
        || itemClass.Contains("optics", StringComparison.Ordinal)
        || itemClass.Contains("barrel", StringComparison.Ordinal);
}
