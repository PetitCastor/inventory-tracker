using InventoryTracker.Model;

namespace InventoryTracker.Tests.Playtest;

/// <summary>
/// A visor, a magazine or an optic sits on the item it belongs to and goes where that item
/// goes — checked against the helmet, weapon and backpack swaps of the 2026-09-25 playtest.
/// </summary>
public sealed class PartsFollowTheirItemTests
{
    [Fact]
    public void A_helmet_stored_at_a_station_takes_its_visor_with_it()
    {
        // 15:57:37 the qrt helmet went to Area18; the Aril came on with its own necksock and visor.
        using var replay = PlaytestReplay.Until("15:58:00");

        AssertOn(replay, "845736965845", "qrt_utility_heavy_helmet_01_01_03", InventoryKind.Location);
        AssertOn(replay, "781903179025", "grin_utility_medium_helmet_01_01_01", InventoryKind.Equipped);
        AssertOn(replay, "781903179026", "grin_utility_medium_helmet_01_01_01", InventoryKind.Equipped);
    }

    [Fact]
    public void A_rifle_stored_in_a_swap_keeps_its_attachments_and_the_new_one_brings_its_own()
    {
        // 20:00:05 the behr sniper went to Area18; 20:00:11 the A03 came on with a magazine and optic.
        using var replay = PlaytestReplay.Until("20:01:00");

        foreach (var part in new[] { "845736965833", "845736965834", "845736965835", "845736965836" })
        {
            AssertOn(replay, part, "behr_sniper_ballistic_01", InventoryKind.Location);
        }

        AssertOn(replay, "781903179031", "gmni_sniper_ballistic_01", InventoryKind.Equipped);
        AssertOn(replay, "781903179032", "gmni_sniper_ballistic_01", InventoryKind.Equipped);
    }

    [Fact]
    public void Guns_stored_with_a_backpack_swap_keep_their_attachments()
    {
        using var replay = PlaytestReplay.Until("20:03:00");

        foreach (var part in new[] { "845736965838", "845736965839", "845736965840", "845736965841" })
        {
            AssertOn(replay, part, "behr_rifle_ballistic_03", InventoryKind.Location);
        }

        AssertOn(replay, "781903179031", "gmni_sniper_ballistic_01", InventoryKind.Location);
        AssertOn(replay, "781903179032", "gmni_sniper_ballistic_01", InventoryKind.Location);
    }

    [Fact]
    public void Nothing_is_orphaned_on_the_character_at_the_end_of_the_day()
    {
        // The player's own account at 20:10: armour, undersuit and backpack on, the qrt helmet
        // with its visor on the head, the cds helmet with its visor and necksock in the backpack.
        using var replay = PlaytestReplay.Until("20:14:50");

        var loose = replay.Holdings
            .Where(h => h.Root?.Kind == InventoryKind.Equipped && h.Chain.Count == 1 && h.Geid is not null)
            .Select(h => h.ItemClass)
            .Where(c => c.Contains("Visor", StringComparison.Ordinal) || c.Contains("necksock", StringComparison.Ordinal)
                        || c.EndsWith("_mag", StringComparison.Ordinal) || c.Contains("optics", StringComparison.Ordinal)
                        || c.Contains("barrel", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(loose);

        AssertOn(replay, "845736965845", "qrt_utility_heavy_helmet_01_01_03", InventoryKind.Equipped);
        AssertOn(replay, "781903176228", "cds_undersuit_helmet_01_01_01", InventoryKind.Equipped);
        Assert.Equal(0, replay.Resolver.ReplayStats.EvictedThenResighted);
    }

    private static void AssertOn(PlaytestReplay replay, string geid, string parentClass, InventoryKind root)
    {
        var row = replay.Named(geid);
        Assert.NotNull(row);
        Assert.Equal($"on {parentClass}", row.Chain[0].Label);
        Assert.Equal(root, row.Root?.Kind);
    }
}
