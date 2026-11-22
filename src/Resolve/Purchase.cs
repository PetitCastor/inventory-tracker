namespace InventoryTracker.Resolve;

/// <summary>One purchase at a shop kiosk, as the item's page shows it.</summary>
/// <param name="Shop">The shop's internal name, e.g. <c>SCShop_LiveFire_Truckstop02-001</c>.</param>
/// <param name="Price">What the whole quantity cost, in aUEC, when the log said.</param>
/// <param name="PlaceLabel">Where the player stood, and so where the item was delivered.</param>
public sealed record Purchase(DateTimeOffset At, string Shop, int Quantity, decimal? Price, string? PlaceLabel);
