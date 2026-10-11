using Microsoft.Data.Sqlite;

namespace InventoryTracker.Naming;

/// <summary>
/// The star-citizen.wiki location list, as cached in <c>wiki_location</c>: the reference a
/// place name is checked against. The wiki does not record the internal strings the logs
/// use, so it can say a name is real but never which internal string it belongs to.
/// </summary>
public sealed class WikiLocations
{
    private readonly Dictionary<string, List<WikiLocation>> _byName;

    public WikiLocations(IEnumerable<WikiLocation> locations)
    {
        _byName = locations
            .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public static readonly WikiLocations Empty = new([]);

    public int Count => _byName.Values.Sum(l => l.Count);

    /// <summary>
    /// The place by that name in that system. Names repeat across systems — there is a
    /// "Stanton Gateway" in Nyx and another in Pyro — so the system has to match too, and a
    /// place whose system is unknown is never backed: the wrong gateway would do.
    /// </summary>
    public WikiLocation? Find(string name, string system)
    {
        if (system == Place.UnknownSystem || !_byName.TryGetValue(name.Trim(), out var matches)) return null;

        return matches.FirstOrDefault(l => string.Equals(l.System, system, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The names of the places in a system a player could store things at, for
    /// suggesting a correction.</summary>
    public IReadOnlyList<string> NamesIn(string system) =>
        _byName.Values
            .SelectMany(l => l)
            .Where(l => system == Place.UnknownSystem
                        || string.Equals(l.System, system, StringComparison.OrdinalIgnoreCase))
            .Where(l => l.Type is not null && Storable.Any(t => l.Type.StartsWith(t, StringComparison.Ordinal)))
            .Select(l => l.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Location types that can hold a local inventory. Asteroids, planets and the
    /// like are on the list too but never have one.</summary>
    private static readonly string[] Storable = ["Manmade", "LandingZone", "Outpost", "PointOfInterest"];

    public static WikiLocations Load(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT name, system, parent, parent_type, type FROM wiki_location";

        var rows = new List<WikiLocation>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new WikiLocation(
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return new WikiLocations(rows);
    }
}
