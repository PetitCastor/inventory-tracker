using InventoryTracker.Model;
using InventoryTracker.Resolve;

namespace InventoryTracker.Tests;

/// <summary>The --holdings listing, as the player reads it in a terminal.</summary>
public class CliTests
{
    private static ItemHolding Row(string itemClass, int quantity, InventoryKind kind, string key, string label) =>
        new(null, itemClass, quantity, [new HoldingLink(kind, key, label)], new DateTimeOffset(2026, 9, 25, 15, 46, 29, TimeSpan.Zero),
            "Move", 1.0, quantity > 1, []);

    [Fact]
    public void A_row_that_stands_for_two_units_says_so_and_the_heading_counts_units()
    {
        // 2026-09-25 15:46: two Defiance helmets on one line, with nothing saying there were two.
        var lines = Cli.FormatGroups([
            Row("slaver_armor_heavy_helmet_01_9tails_01", 2, InventoryKind.Location, "2273540638", "Area18"),
            Row("grin_utility_medium_helmet_01_01_01", 1, InventoryKind.Location, "2273540638", "Area18"),
        ]).ToList();

        Assert.Contains("  Area18 [2273540638]  (3)", lines);
        var two = lines.Single(l => l.Contains("slaver_armor_heavy_helmet_01_9tails_01", StringComparison.Ordinal));
        var one = lines.Single(l => l.Contains("grin_utility_medium_helmet_01_01_01", StringComparison.Ordinal));
        Assert.StartsWith("    2×  slaver_armor_heavy_helmet_01_9tails_01 ", two);
        Assert.StartsWith("        grin_utility_medium_helmet_01_01_01 ", one);

        // The count has a column of its own: the rest of both rows lines up.
        Assert.Equal(two.IndexOf("Area18", StringComparison.Ordinal), one.IndexOf("Area18", StringComparison.Ordinal));
    }

    [Fact]
    public void Groups_are_ordered_by_units_not_by_lines()
    {
        var lines = Cli.FormatGroups([
            Row("behr_rifle_ballistic_03_mag", 5, InventoryKind.Location, "2273540638", "Area18"),
            Row("a", 1, InventoryKind.Location, "4005457614", "Lorville"),
            Row("b", 1, InventoryKind.Location, "4005457614", "Lorville"),
            Row("c", 1, InventoryKind.Location, "4005457614", "Lorville"),
        ]).Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("   ", StringComparison.Ordinal)).ToList();

        Assert.Equal(["  Area18 [2273540638]  (5)", "  Lorville [4005457614]  (3)"], lines);
    }

    [Fact]
    public void Everything_on_the_character_is_one_group_whichever_row_sorts_first()
    {
        var equipped = Row("qrt_utility_heavy_helmet_01_01_03", 1, InventoryKind.Equipped, "", "equipped on your character");
        var carried = Row("cds_undersuit_helmet_01_01_01", 1, InventoryKind.Equipped, "", "carried on your character");

        foreach (var order in new[] { new[] { equipped, carried }, [carried, equipped] })
        {
            Assert.Contains("  on your character  (2)", Cli.FormatGroups(order).ToList());
        }
    }
}
