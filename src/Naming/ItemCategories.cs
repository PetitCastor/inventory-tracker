namespace InventoryTracker.Naming;

/// <summary>
/// Sorts items into an <see cref="ItemCategory"/> by the wiki's <c>type_label</c>, the only
/// classification the tracker has for an item. The labels are the wiki's own ("Torso (Armor)",
/// "FPS Weapon", "Power Plant"…), so a label not listed here falls into
/// <see cref="ItemCategory.Other"/> rather than being guessed at.
/// </summary>
public static class ItemCategories
{
    private static readonly HashSet<string> Armor = new(StringComparer.OrdinalIgnoreCase)
    {
        "Armor", "Backpack", "Suit",
    };

    private static readonly HashSet<string> Clothing = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jacket", "Legs", "Shirt", "Shoes", "Hat", "Gloves",
        "Char Accessory Eyes", "Char Head Piercings",
    };

    private static readonly HashSet<string> Weapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "FPS Weapon", "Weapon Attachment", "Grenade",
    };

    private static readonly HashSet<string> Consumables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Food", "Drink", "Bottle", "Fps Consumable",
    };

    // Only parts that are fitted to a ship and nothing else. "Mining Modifier" is left out: the
    // wiki uses it for the multitool's modules as well as the mining head's.
    private static readonly HashSet<string> ShipParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Power Plant", "Cooler", "Shield", "Quantum Drive", "Jump Drive", "Radar",
        "Weapon Gun", "Weapon Defensive", "Weapon Mining", "Missile", "Missile Launcher",
        "Bomb", "Bomb Launcher", "Turret", "Utility Turret", "Fuel Tank", "Quantum Fuel Tank",
        "External Fuel Tank", "Fuel Intake", "Main Thruster", "Manneuver Thruster",
        "Life Support Generator", "Quantum Interdiction Generator", "E M P", "Tractor Beam",
        "Salvage Head", "Salvage Modifier",
    };

    /// <summary>Every category, in the order the filters list them.</summary>
    public static IReadOnlyList<ItemCategory> All { get; } = Enum.GetValues<ItemCategory>();

    /// <summary>The category of an item whose wiki type label is <paramref name="typeLabel"/>;
    /// <see cref="ItemCategory.Other"/> when the wiki does not know the item (no label).</summary>
    public static ItemCategory Of(string? typeLabel)
    {
        if (string.IsNullOrWhiteSpace(typeLabel)) return ItemCategory.Other;

        var label = typeLabel.Trim();
        if (label.EndsWith("(Armor)", StringComparison.OrdinalIgnoreCase) || Armor.Contains(label))
            return ItemCategory.Armor;
        if (Clothing.Contains(label)) return ItemCategory.Clothing;
        if (Weapons.Contains(label)) return ItemCategory.Weapons;
        if (Consumables.Contains(label)) return ItemCategory.Consumables;
        if (ShipParts.Contains(label)) return ItemCategory.ShipParts;
        return ItemCategory.Other;
    }

    /// <summary>The name a filter shows for <paramref name="category"/>.</summary>
    public static string Label(ItemCategory category) => category switch
    {
        ItemCategory.ShipParts => "Ship parts",
        _ => category.ToString(),
    };
}
