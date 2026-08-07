using System.Text.Json;
using System.Text.RegularExpressions;
using InventoryTracker.Data;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Naming;

/// <summary>A hand-written correction for a place the logs never corroborated.</summary>
public sealed class PlaceOverride
{
    public string? Name { get; set; }
    public string? System { get; set; }
}

/// <summary>
/// Somewhere with a local inventory, as the player would name it.
/// </summary>
/// <param name="Id">The canonical location id — the alias with the most sightings.</param>
/// <param name="AliasIds">Every id that resolves here, including <paramref name="Id"/>. The
/// game issues a fresh id for the same station from time to time, and items stored under
/// each would otherwise show up as two separate places.</param>
/// <param name="RawName">The internal string the game paired with the id, kept for diagnostics.</param>
public sealed record Place(
    string Id,
    IReadOnlyList<string> AliasIds,
    string Name,
    string System,
    string? RawName,
    bool NameVerified,
    bool SystemVerified)
{
    public const string UnknownSystem = "Unknown system";
}

/// <summary>
/// Resolves location ids to the system and place name a player would recognise.
/// <para>
/// The string the game pairs with a location id is a legacy asset name, not a place name:
/// id 141810852 is logged as "RR_JP_NyxCastra" but the game calls it "Stanton Gateway" and
/// it orbits in Nyx. Neither fact is recoverable from the string, so both are voted on from
/// corroborating sightings gathered while the player was standing there — the display name
/// from &lt;Calculate Route&gt;, the system from the object-container paths that streamed in
/// on arrival.
/// </para>
/// </summary>
public sealed partial class PlaceCatalog
{
    private readonly Dictionary<string, Place> _byLocationId;

    /// <summary>Stations worth browsing — the ids the game paired with a local inventory.</summary>
    public IReadOnlyList<Place> Places { get; }

    private PlaceCatalog(IReadOnlyList<Place> places, Dictionary<string, Place> byLocationId)
    {
        Places = places;
        _byLocationId = byLocationId;
    }

    /// <summary>The place an id belongs to, or null when the id was never seen named.</summary>
    public Place? ByLocationId(string? id) =>
        id is not null && _byLocationId.TryGetValue(id, out var p) ? p : null;

    /// <summary>A stand-in for a location id that holds items but was never named.</summary>
    public static Place Unnamed(string id) =>
        new(id, [id], $"Unnamed place {id}", Place.UnknownSystem, null, false, false);

    /// <summary>Where hand-written corrections live, alongside the store they correct.</summary>
    public static string OverridePathFor(TrackerDb db) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.Path)) ?? ".", "place_override.json");

    public static PlaceCatalog Load(TrackerDb db)
    {
        using var cn = db.Open();
        return Build(
            LoadRawNames(cn),
            LoadEvidence(cn, "name"),
            LoadEvidence(cn, "system"),
            LoadOverrides(OverridePathFor(db)));
    }

    private static PlaceCatalog Build(
        Dictionary<string, (string Raw, int Evidence)> rawNames,
        Dictionary<string, List<(string Value, int Hits)>> nameVotes,
        Dictionary<string, List<(string Value, int Hits)>> systemVotes,
        Dictionary<string, PlaceOverride> overrides)
    {
        // Sightings also attach to the pseudo-ids the client uses in open space ("Hurston",
        // "Aberdeen"). Those are resolvable but are not stations, so they are named on
        // request and kept out of the browsable list.
        var ids = rawNames.Keys
            .Concat(overrides.Keys)
            .Concat(nameVotes.Keys)
            .Concat(systemVotes.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The game re-issues an id for the same station from time to time. The internal
        // string is what says two ids are the same place — the player-facing name does not,
        // because the gateways to Nyx from Stanton and from Pyro are both "Nyx Gateway".
        var groups = ids.GroupBy(
            id => rawNames.TryGetValue(id, out var raw) ? raw.Raw : $" id:{id}",
            StringComparer.OrdinalIgnoreCase);

        var places = new List<Place>();
        var stations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var aliases = group
                .OrderByDescending(id => rawNames.GetValueOrDefault(id).Evidence)
                .ThenBy(id => id, StringComparer.Ordinal)
                .ToList();

            var canonical = aliases[0];
            var raw = rawNames.GetValueOrDefault(canonical).Raw;
            var over = aliases.Select(overrides.GetValueOrDefault).FirstOrDefault(o => o is not null);

            var name = over?.Name ?? Winner(aliases, nameVotes);
            var nameVerified = name is not null;
            name ??= raw;
            if (string.IsNullOrWhiteSpace(name)) continue;

            // The internal string beats streamed assets here, which is the opposite of how
            // the name resolves. Arriving anywhere by jump streams in both systems' object
            // containers, so at a gateway the asset evidence is a coin flip — while the
            // internal name encodes the host system correctly even when its place name is
            // long obsolete.
            var system = over?.System
                ?? (raw is null ? null : SystemFromRaw(raw))
                ?? Winner(aliases, systemVotes)
                ?? Place.UnknownSystem;

            var place = new Place(
                canonical, aliases, name, system, raw, nameVerified, system != Place.UnknownSystem);

            places.Add(place);

            // Only somewhere the game paired with an internal inventory name is a station
            // the player can browse; the rest are just names for ids seen in passing.
            if (raw is not null || over is not null) stations.Add(canonical);
        }

        var index = new Dictionary<string, Place>(StringComparer.Ordinal);
        foreach (var place in places)
        {
            foreach (var alias in place.AliasIds) index[alias] = place;
        }

        var browsable = places
            .Where(p => stations.Contains(p.Id))
            .OrderBy(p => p.System, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PlaceCatalog(browsable, index);
    }

    /// <summary>The most-sighted value across every id that resolves to the same place.</summary>
    private static string? Winner(
        IEnumerable<string> aliases, Dictionary<string, List<(string Value, int Hits)>> votes)
    {
        var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in aliases)
        {
            if (!votes.TryGetValue(id, out var rows)) continue;
            foreach (var (value, hits) in rows)
            {
                tally[value] = tally.GetValueOrDefault(value) + hits;
            }
        }

        return tally.Count == 0
            ? null
            : tally.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
    }

    // "RR_JP_NyxCastra" is the Nyx-side stop of the Nyx<->Castra jump point; "RR_JP_PyroNyx"
    // is the far end of a different one, in Pyro. The first system named is the host.
    [GeneratedRegex(@"_JP_(?<host>[A-Z][a-z]+)[A-Z][a-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex JumpPointPair();

    // Rest stops name Stanton's planets by abbreviation, and Pyro's by number ("P5" is Pyro V).
    private static readonly Dictionary<string, string> PlanetAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HUR"] = "Stanton",
        ["CRU"] = "Stanton",
        ["ARC"] = "Stanton",
        ["MIC"] = "Stanton",
        ["PYR"] = "Pyro",
        ["P"] = "Pyro",
    };

    /// <summary>
    /// The host system named by the internal string. Legacy asset names get the place's
    /// <i>name</i> wrong — "RR_JP_NyxCastra" is called Stanton Gateway in game — but the
    /// system they sit in is still encoded correctly, which is why this is preferred over
    /// streamed-asset evidence. Anything it cannot decide is left explicitly unknown rather
    /// than guessed, so it surfaces in diagnostics and can be fixed in place_override.json.
    /// </summary>
    internal static string? SystemFromRaw(string raw)
    {
        var jp = JumpPointPair().Match(raw);
        if (jp.Success && Systems.IsSystem(jp.Groups["host"].Value))
        {
            return Systems.Canonical(jp.Groups["host"].Value);
        }

        foreach (var token in raw.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            // "Stanton1" is a planet of Stanton; "P5" is a planet of Pyro.
            var letters = token.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (letters.Length == 0) continue;

            if (Systems.IsSystem(letters)) return Systems.Canonical(letters);
            if (letters.Length < token.Length && PlanetAbbreviations.TryGetValue(letters, out var byNumber)) return byNumber;
            if (PlanetAbbreviations.TryGetValue(token, out var mapped)) return mapped;
        }

        return null;
    }

    private static Dictionary<string, (string Raw, int Evidence)> LoadRawNames(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT location_id, name, evidence_count FROM location_name";

        var map = new Dictionary<string, (string, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = (r.GetString(1), r.GetInt32(2));
        return map;
    }

    /// <summary>Every sighting of one kind, per location id, so aliases can be tallied together.</summary>
    private static Dictionary<string, List<(string Value, int Hits)>> LoadEvidence(
        SqliteConnection cn, string kind)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT location_id, value, hits FROM place_evidence WHERE kind = $k";
        cmd.Parameters.AddWithValue("$k", kind);

        var map = new Dictionary<string, List<(string, int)>>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetString(0);
            if (!map.TryGetValue(id, out var rows)) map[id] = rows = [];
            rows.Add((r.GetString(1), r.GetInt32(2)));
        }
        return map;
    }

    private static Dictionary<string, PlaceOverride> LoadOverrides(string path)
    {
        if (!File.Exists(path)) return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, PlaceOverride>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A malformed override file must not take the whole catalogue down with it.
            return [];
        }
    }
}
