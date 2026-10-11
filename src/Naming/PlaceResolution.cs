namespace InventoryTracker.Naming;

/// <summary>
/// What <see cref="PlaceNameResolver"/> made of a place: the name it settled on, how sure it
/// is, and which sources back that name.
/// </summary>
/// <param name="Name">Null when no source proposed anything.</param>
/// <param name="Planet">The planet or moon the wiki puts the place on, when it does.</param>
public sealed record PlaceResolution(
    string? Name,
    PlaceNameStatus Status,
    PlaceNameSource Sources,
    string? Planet = null)
{
    public static readonly PlaceResolution Unresolved = new(null, PlaceNameStatus.Unresolved, PlaceNameSource.None);
}
