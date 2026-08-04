using System.Text.RegularExpressions;

namespace InventoryTracker.Model;

/// <summary>
/// Splits the game's "&lt;class&gt;_&lt;geid&gt;" entity strings, e.g.
/// "grin_multitool_01_red01_481104483777" -> ("grin_multitool_01_red01", "481104483777").
/// </summary>
public static partial class EntityId
{
    // Lazy on the class part: class names routinely end in digits ("..._01_01_01"),
    // so a greedy match would slice a 12-digit geid in half and keep the tail.
    [GeneratedRegex(@"^(?<cls>.+?)_(?<geid>\d{6,})$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassAndGeid();

    public static bool TrySplit(string? entity, out string className, out string geid)
    {
        className = "";
        geid = "";
        if (string.IsNullOrWhiteSpace(entity)) return false;

        entity = entity.Trim();
        if (entity is "NULL" or "NONE" or "INVALID") return false;

        var m = ClassAndGeid().Match(entity);
        if (!m.Success) return false;

        className = m.Groups["cls"].Value;
        geid = m.Groups["geid"].Value;
        return true;
    }
}
