namespace InventoryTracker.Resolve;

/// <summary>
/// A character reset as the player would describe it: when it happened, the home location they
/// chose, and how many items the tracker knew about before it and cleared.
/// </summary>
/// <param name="ResetAt">When the character creator opened.</param>
/// <param name="Build">The game build the reset came with.</param>
/// <param name="Home">The new home location, named, or null when the logs do not say.</param>
/// <param name="Cleared">Units the tracker held before the reset, all of which it cleared.</param>
public sealed record CharacterResetReport(DateTimeOffset ResetAt, int Build, string? Home, int Cleared);
