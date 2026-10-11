namespace InventoryTracker.Naming;

/// <summary>A place on the star-citizen.wiki location list.</summary>
/// <param name="System">Canonical system name ("Nyx"), without the API's " System" suffix.</param>
/// <param name="Parent">What the place orbits or sits on: a star, planet, moon or station.</param>
/// <param name="ParentType">The parent's type as the API names it: "Star", "Planet", "Moon"…</param>
/// <param name="Type">The place's own type: "Manmade", "LandingZone", "Outpost"…</param>
public sealed record WikiLocation(
    string Name,
    string System,
    string? Parent,
    string? ParentType,
    string? Type)
{
    /// <summary>The planet or moon the place is on; null when it orbits a star or sits on
    /// another station.</summary>
    public string? Planet =>
        ParentType is "Planet" or "Moon" ? Parent : null;
}
