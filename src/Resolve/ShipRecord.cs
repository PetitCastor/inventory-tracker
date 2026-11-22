namespace InventoryTracker.Resolve;

/// <summary>
/// A ship as the <c>vehicle</c> table knows it. <paramref name="ClassName"/> comes from leaving its
/// pilot seat, the retrieval from calling it out of storage; a ship seen only one way lacks the other.
/// </summary>
public sealed record ShipRecord(
    long Geid,
    string? ClassName,
    DateTimeOffset? LastSeen,
    DateTimeOffset? RetrievedAt,
    string? RetrievedLocationId);
