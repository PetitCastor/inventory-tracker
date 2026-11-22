namespace InventoryTracker.Resolve;

/// <summary>
/// How an item came into the player's hands, as far as the logs say. It travels with the item
/// through every later move, so a magazine bought at a kiosk and then put in a backpack is
/// still a bought one. Shown as a tag beside the item's category.
/// </summary>
public enum ItemOrigin
{
    /// <summary>The logs never said: looted, rewarded, or owned before the logs begin.</summary>
    Unknown,

    /// <summary>Bought at a shop kiosk.</summary>
    Bought,
}
