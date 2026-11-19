namespace InventoryTracker.Resolve;

/// <summary>
/// A character reset as the player would describe it: when it happened, the home location they
/// chose, and how many items the tracker knew about and gathered there.
/// </summary>
/// <param name="ResetAt">When the character creator opened.</param>
/// <param name="Build">The game build the reset came with.</param>
/// <param name="Home">The new home location, named, or null when the logs do not say.</param>
/// <param name="Gathered">Units the tracker moved to the home location.</param>
/// <param name="Cleared">
/// Units the tracker cleared instead, because the logs did not name the home location.
/// </param>
public sealed record CharacterResetReport(DateTimeOffset ResetAt, int Build, string? Home, int Gathered, int Cleared);
