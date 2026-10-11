using System.Text.RegularExpressions;

namespace InventoryTracker.Naming;

/// <summary>
/// Reads which localisation entry names a place from the internal string the logs pair with
/// its inventory. Some strings are the key itself ("Stanton3_Area18", "RR_CRU_L4"); the rest
/// follow a handful of patterns, each checked against the game's own global.ini:
/// <list type="bullet">
/// <item>"RR_P6_LEO" is "RR_P6" (Ruin Station); Stanton's low-orbit stations are its planets'
/// transfer points, "RR_ARC_LEO" being "Stanton3_Transfer" (Baijini Point).</item>
/// <item>"RR_JP_NyxPyro", the gateway in Nyx towards Pyro, is "Nyx_Pyro_JPStation".</item>
/// <item>"AsteroidClusterBase_Nyx_Social_Keeger_001" is "Nyx_SocialStation_001" (People's
/// Service Station Alpha) — confirmed by the logs for _003, which the game calls Theta.</item>
/// </list>
/// Nothing here is a name: a pattern only says where to look, and a key that is not in the
/// file proposes nothing.
/// </summary>
public static partial class PlaceTextKeys
{
    [GeneratedRegex(@"^R[RS]_(?<body>[A-Za-z]+[0-9]?)_LEO$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowOrbit();

    [GeneratedRegex(@"^R[RS]_JP_(?<from>[A-Z][a-z]+)(?<to>[A-Z][a-z]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex JumpPoint();

    [GeneratedRegex(@"^AsteroidClusterBase_(?<system>[A-Za-z]+)_Social_[A-Za-z]+_(?<n>[0-9]+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SocialStation();

    /// <summary>Stanton's planets by the abbreviation station strings use, and their orbit.</summary>
    private static readonly Dictionary<string, int> StantonOrbits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HUR"] = 1,
        ["CRU"] = 2,
        ["ARC"] = 3,
        ["MIC"] = 4,
    };

    /// <summary>The name the game's text gives the place, or null when it has none.</summary>
    public static string? NameFrom(string raw, GameLocalization text)
    {
        if (text.Get(raw) is { } exact) return exact;

        var orbit = LowOrbit().Match(raw);
        if (orbit.Success)
        {
            var body = orbit.Groups["body"].Value;
            if (text.Get($"RR_{body}") is { } station) return station;

            if (StantonOrbits.TryGetValue(body, out var n))
            {
                return text.Get($"Stanton{n}_Transfer") ?? text.GetOnlyChild($"Stanton{n}_Transfer");
            }

            return null;
        }

        var jump = JumpPoint().Match(raw);
        if (jump.Success)
        {
            return text.Get($"{jump.Groups["from"].Value}_{jump.Groups["to"].Value}_JPStation");
        }

        var social = SocialStation().Match(raw);
        if (social.Success)
        {
            return text.Get($"{social.Groups["system"].Value}_SocialStation_{social.Groups["n"].Value}");
        }

        return null;
    }
}
