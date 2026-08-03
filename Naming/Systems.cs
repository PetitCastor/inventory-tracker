namespace LogParser.Naming;

/// <summary>
/// The solar systems the game can stream, and how to recognise one in the strings it logs.
/// <para>
/// This is deliberately a closed list. Object-container paths interleave systems with asset
/// categories at the same depth — <c>loc/mod/nyx/...</c> is a system but <c>loc/mod/ugf/...</c>,
/// <c>loc/common/...</c> and <c>loc/flagship/...</c> are not — so the segment can only be
/// trusted when it is a name we recognise.
/// </para>
/// </summary>
public static class Systems
{
    /// <summary>Lower-cased system names as they appear in asset paths.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "stanton", "pyro", "nyx", "castra", "terra", "magnus", "sol", "odin",
        "ellis", "kilian", "oberon", "helios", "leir", "tayac", "charon", "cano",
        "rhetor", "hadrian", "davien", "kellog", "goss", "corel", "gliese", "banshee",
    };

    /// <summary>Display casing for the systems we expect to actually show up.</summary>
    private static readonly Dictionary<string, string> Display = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stanton"] = "Stanton",
        ["pyro"] = "Pyro",
        ["nyx"] = "Nyx",
        ["castra"] = "Castra",
        ["terra"] = "Terra",
        ["magnus"] = "Magnus",
        ["sol"] = "Sol",
    };

    public static bool IsSystem(string? token) =>
        token is not null && Known.Contains(token);

    /// <summary>Canonical display form, e.g. "nyx" -> "Nyx".</summary>
    public static string Canonical(string token) =>
        Display.TryGetValue(token, out var d)
            ? d
            : string.Concat(char.ToUpperInvariant(token[0]), token[1..].ToLowerInvariant());

    /// <summary>
    /// The host system named by an object-container path, or null when the path names no
    /// system we recognise. Handles both <c>loc/mod/&lt;system&gt;/</c> (stations, rest stops)
    /// and <c>loc/flagship/&lt;system&gt;/</c> (landing zones).
    /// </summary>
    public static string? FromAssetPath(string text)
    {
        const string marker = "objectcontainers/pu/loc/";

        var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        // Look at the two segments after "loc/": either is the system, depending on
        // whether an asset category ("mod", "flagship") sits in between.
        var rest = text[(at + marker.Length)..];
        var seg = rest.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);

        foreach (var candidate in seg.Take(2))
        {
            if (IsSystem(candidate)) return Canonical(candidate);
        }

        return null;
    }

    /// <summary>
    /// True when a player-facing place name is really just the system (or a bare celestial
    /// body the player was flying past) rather than somewhere with an inventory. The game
    /// reports these while in space, so they must never be adopted as a station's name.
    /// </summary>
    public static bool IsSystemOnly(string displayName)
    {
        var name = displayName.Trim();
        if (name.EndsWith(" System", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^" System".Length].Trim();
        }
        return IsSystem(name);
    }
}
