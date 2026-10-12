namespace InventoryTracker.Resolve;

/// <summary>
/// The last time a ship of a class was called out of storage, and where: the best lead there
/// is on where it is now, since storing it and flying it about write no location of their own.
/// </summary>
public sealed record ShipRetrieval(DateTimeOffset At, string? LocationId, string? PlaceLabel);
