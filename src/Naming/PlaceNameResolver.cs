namespace InventoryTracker.Naming;

/// <summary>
/// Decides what a place is called, and whether that name can be shown as fact.
/// <para>
/// Three sources propose a name: the game naming the place in the logs (a route plotted from
/// inside it), the game's own text under a key read from the internal string, and the
/// hand-checked <see cref="KnownPlaces"/> table. The wiki's location list then backs any
/// proposal it has a place for in the same system. A name two sources agree on is confirmed;
/// anything less is pending, and so is a name another equally backed one contradicts.
/// </para>
/// </summary>
public static class PlaceNameResolver
{
    /// <summary>Proposing sources, most trusted first: the order that breaks a tie.</summary>
    private static readonly PlaceNameSource[] Proposers =
        [PlaceNameSource.Log, PlaceNameSource.GameText, PlaceNameSource.KnownPlaces];

    /// <param name="raw">The internal string the game paired with the place's inventory;
    /// null for a place the logs only ever named in passing.</param>
    /// <param name="system">Where the place is, or <see cref="Place.UnknownSystem"/>.</param>
    /// <param name="loggedName">The name the logs voted for, if any.</param>
    public static PlaceResolution Resolve(
        string? raw, string system, string? loggedName, GameLocalization text, WikiLocations wiki)
    {
        var proposals = new List<(string Name, PlaceNameSource Source)>();

        if (!string.IsNullOrWhiteSpace(loggedName)) proposals.Add((loggedName.Trim(), PlaceNameSource.Log));

        if (raw is not null)
        {
            if (PlaceTextKeys.NameFrom(raw, text) is { } fromText) proposals.Add((fromText, PlaceNameSource.GameText));
            if (KnownPlaces.FromRaw(raw) is { } known) proposals.Add((known.Place, PlaceNameSource.KnownPlaces));
        }

        if (proposals.Count == 0) return PlaceResolution.Unresolved;

        var candidates = proposals
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var sources = g.Aggregate(PlaceNameSource.None, (all, p) => all | p.Source);
                var onWiki = wiki.Find(g.Key, system);
                if (onWiki is not null) sources |= PlaceNameSource.Wiki;

                // The wiki's spelling wins over a proposer's: it is what the place is listed as.
                return (Name: onWiki?.Name ?? g.First().Name, Sources: sources, Planet: onWiki?.Planet);
            })
            .OrderByDescending(c => Support(c.Sources))
            .ThenBy(c => Rank(c.Sources))
            .ToList();

        var best = candidates[0];
        var contested = candidates.Skip(1).Any(c => Support(c.Sources) >= 2 && Support(c.Sources) == Support(best.Sources));
        var status = Support(best.Sources) >= 2 && !contested ? PlaceNameStatus.Confirmed : PlaceNameStatus.Pending;

        return new PlaceResolution(best.Name, status, best.Sources, best.Planet);
    }

    private static int Support(PlaceNameSource sources) =>
        System.Numerics.BitOperations.PopCount((uint)sources);

    /// <summary>The position of the most trusted proposer behind a candidate.</summary>
    private static int Rank(PlaceNameSource sources) =>
        Array.FindIndex(Proposers, p => sources.HasFlag(p)) is var i and >= 0 ? i : Proposers.Length;
}
