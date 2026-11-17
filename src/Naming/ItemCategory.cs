namespace InventoryTracker.Naming;

/// <summary>
/// A broad kind of item, for the quick filters on the Locations page. Declared in the order the
/// filters are shown.
/// </summary>
public enum ItemCategory
{
    Armor,
    Clothing,
    Weapons,
    Consumables,
    ShipParts,

    /// <summary>Everything else, including any item the wiki catalogue does not list.</summary>
    Other,
}
