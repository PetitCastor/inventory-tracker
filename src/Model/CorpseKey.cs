using System.Globalization;

namespace InventoryTracker.Model;

/// <summary>
/// Names one death: when it happened and where the player was. Each death leaves its own
/// body, so two deaths at the same station are two corpses, not one.
/// <para>
/// Written as <c>&lt;unix ms&gt;:&lt;location id&gt;</c>, with an empty location id when the
/// logs never said where the player was. A location id is all digits, so the first colon
/// always ends the time.
/// </para>
/// </summary>
public static class CorpseKey
{
    public static string Format(DateTimeOffset at, string? locationId) =>
        $"{at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}:{locationId ?? ""}";

    /// <summary>Reads a key back into when and where; false for anything else.</summary>
    public static bool TryParse(string key, out DateTimeOffset at, out string locationId)
    {
        at = default;
        locationId = "";

        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return false;
        if (!long.TryParse(key.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) return false;

        at = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        locationId = key[(colon + 1)..];
        return true;
    }
}
