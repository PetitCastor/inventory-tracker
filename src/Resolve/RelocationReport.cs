namespace InventoryTracker.Resolve;

/// <summary>
/// What one <see cref="LedgerReplay.Relocation"/> did: the units it took out of each holding
/// and moved to <paramref name="Destination"/>. A holding it found empty is not listed.
/// </summary>
public sealed record RelocationReport(
    DateTimeOffset At, int Build, Holding Destination, IReadOnlyDictionary<Holding, int> From);
