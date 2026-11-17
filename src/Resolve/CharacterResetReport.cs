namespace InventoryTracker.Resolve;

/// <summary>
/// A character reset as the player would describe it: when it happened, the home location they
/// chose, and where the items the tracker knew about were taken from to get there.
/// </summary>
/// <param name="ResetAt">When the character creator opened.</param>
/// <param name="Build">The game build the reset came with.</param>
/// <param name="Home">The new home location, named.</param>
/// <param name="From">Each place emptied into it, named, with how many units it held; largest first.</param>
public sealed record CharacterResetReport(
    DateTimeOffset ResetAt, int Build, string Home, IReadOnlyList<CharacterResetOrigin> From)
{
    /// <summary>Units the reset moved, all origins together.</summary>
    public int Moved => From.Sum(f => f.Units);
}
