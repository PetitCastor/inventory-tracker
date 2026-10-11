namespace InventoryTracker.Naming;

/// <summary>Where a place name came from. A name backed by two of these is confirmed.</summary>
[Flags]
public enum PlaceNameSource
{
    None = 0,

    /// <summary>The game named the place itself: a route plotted from inside it
    /// ("Projected Start Location is X").</summary>
    Log = 1,

    /// <summary>The game's own localisation file, under a key read from the internal string.</summary>
    GameText = 2,

    /// <summary>The hand-checked table in <see cref="KnownPlaces"/>.</summary>
    KnownPlaces = 4,

    /// <summary>The star-citizen.wiki location list has a place by that name in that system.</summary>
    Wiki = 8,

    /// <summary>The player confirmed or corrected the name.</summary>
    User = 16,
}
