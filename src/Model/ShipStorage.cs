using System.Text.RegularExpressions;

namespace InventoryTracker.Model;

/// <summary>
/// Recognises a ship's own storage among containers: <c>PersonalStorage_GLSN_Shiv-000</c> is
/// the first personal locker of a Grey's Shiv.
/// <para>
/// In the 2026-10-12 test the Shiv's two lockers kept their geids (876737713105, 876737713106)
/// in the hangar, in flight, and across a store and a retrieve of the ship, and what was in
/// them stayed. So a locker is followed like any container, but it belongs to the ship, not
/// to the station it was last opened at. No line ties a locker to the ship's own geid; the
/// class in its name is the link.
/// </para>
/// </summary>
public static partial class ShipStorage
{
    [GeneratedRegex(@"^PersonalStorage_(?<ship>.+)-\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex Locker();

    /// <summary>The ship class a container class is a locker of, e.g. "GLSN_Shiv".</summary>
    public static bool TryShipClass(string? containerClass, out string shipClass)
    {
        shipClass = "";
        if (containerClass is null) return false;

        var m = Locker().Match(containerClass);
        if (!m.Success) return false;

        shipClass = m.Groups["ship"].Value;
        return true;
    }
}
