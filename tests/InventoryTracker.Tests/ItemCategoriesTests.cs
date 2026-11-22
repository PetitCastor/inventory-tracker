using InventoryTracker.Naming;

namespace InventoryTracker.Tests;

/// <summary>
/// The quick filters on the Locations page sort items by the wiki's type label. A label the
/// tracker has no rule for, or an item the wiki does not know, lands in Other.
/// </summary>
public class ItemCategoriesTests
{
    [Theory]
    [InlineData("Helmet (Armor)", ItemCategory.Armor)]
    [InlineData("Torso (Armor)", ItemCategory.Armor)]
    [InlineData("Undersuit (Armor)", ItemCategory.Armor)]
    [InlineData("Backpack (Armor)", ItemCategory.Armor)]
    [InlineData("Armor", ItemCategory.Armor)]
    [InlineData("Jacket", ItemCategory.Clothing)]
    [InlineData("Hat", ItemCategory.Clothing)]
    [InlineData("Char Accessory Eyes", ItemCategory.Clothing)]
    [InlineData("FPS Weapon", ItemCategory.Weapons)]
    [InlineData("Weapon Attachment", ItemCategory.Weapons)]
    [InlineData("Drink", ItemCategory.Consumables)]
    [InlineData("Fps Consumable", ItemCategory.Consumables)]
    [InlineData("Power Plant", ItemCategory.ShipParts)]
    [InlineData("Weapon Gun", ItemCategory.ShipParts)]
    [InlineData("Quantum Drive", ItemCategory.ShipParts)]
    [InlineData("Mining Modifier", ItemCategory.Other)]
    [InlineData("Paints", ItemCategory.Other)]
    [InlineData("Misc", ItemCategory.Other)]
    public void A_wiki_type_label_sorts_into_its_category(string typeLabel, ItemCategory expected)
    {
        Assert.Equal(expected, ItemCategories.Of(typeLabel));
    }

    [Fact]
    public void Labels_are_matched_regardless_of_case_and_surrounding_spaces()
    {
        Assert.Equal(ItemCategory.Weapons, ItemCategories.Of("  fps weapon "));
        Assert.Equal(ItemCategory.Armor, ItemCategories.Of("legs (armor)"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_item_the_wiki_does_not_know_is_other(string? typeLabel)
    {
        Assert.Equal(ItemCategory.Other, ItemCategories.Of(typeLabel));
    }

    [Fact]
    public void Clothing_legs_are_not_armor_legs()
    {
        Assert.Equal(ItemCategory.Clothing, ItemCategories.Of("Legs"));
        Assert.Equal(ItemCategory.Armor, ItemCategories.Of("Legs (Armor)"));
    }

    [Theory]
    [InlineData("crlf_medgun_vial_01")]
    [InlineData("grin_multitool_resource_healing_01")]
    public void A_healing_refill_is_a_consumable_though_the_wiki_calls_it_a_weapon_attachment(string itemClass)
    {
        Assert.Equal(ItemCategory.Consumables, ItemCategories.Of("Weapon Attachment", itemClass));
    }

    [Theory]
    [InlineData("behr_rifle_ballistic_03_mag")]
    [InlineData("crlf_medgun_01")]
    [InlineData(null)]
    public void Other_items_still_go_by_their_wiki_label(string? itemClass)
    {
        Assert.Equal(ItemCategory.Weapons, ItemCategories.Of("Weapon Attachment", itemClass));
        Assert.Equal(ItemCategory.Weapons, ItemCategories.Of("FPS Weapon", itemClass));
    }

    [Fact]
    public void Every_category_has_a_label()
    {
        Assert.Equal("Ship parts", ItemCategories.Label(ItemCategory.ShipParts));
        Assert.All(ItemCategories.All, c => Assert.False(string.IsNullOrWhiteSpace(ItemCategories.Label(c))));
    }
}
