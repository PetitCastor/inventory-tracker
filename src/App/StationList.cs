using InventoryTracker.Naming;

namespace InventoryTracker.App;

/// <summary>
/// The stations the player can browse: the Locations picker and the command palette offer the
/// same list, so a station the palette can jump to is always one the picker can show.
/// </summary>
public static class StationList
{
    /// <summary>
    /// Every station holding something now, plus <paramref name="alsoInclude"/> when given.
    /// Unordered; callers group and sort it for their own display.
    /// </summary>
    public static IReadOnlyList<Place> Holding(TrackerState.Snapshot snapshot, Place? alsoInclude = null)
    {
        var catalogue = snapshot.Resolver?.Places;
        if (catalogue is null) return [];

        // The catalogue lists every station the game ever paired with a local-inventory name,
        // whether or not anything was ever recorded there since the tracker's wipe date.
        // Only places holding something now are offered, so the list doesn't fill up with
        // stations the player has simply never visited.
        var holdingKeys = snapshot.Holdings
            .Where(h => h.Holding.IsAtStation)
            .Select(h => h.Holding.Root!.Key)
            .ToHashSet(StringComparer.Ordinal);

        var known = catalogue.Places.SelectMany(p => p.AliasIds).ToHashSet(StringComparer.Ordinal);
        var orphans = holdingKeys
            .Where(key => !known.Contains(key))
            .Select(id => catalogue.ByLocationId(id) ?? PlaceCatalog.Unnamed(id))
            .DistinctBy(p => p.Id);

        return catalogue.Places
            .Where(p => p.AliasIds.Any(holdingKeys.Contains) || p.Id == alsoInclude?.Id)
            .Concat(orphans)
            .ToList();
    }
}
