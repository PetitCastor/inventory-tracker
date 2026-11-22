using System.Globalization;
using InventoryTracker.Model;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Resolve;

/// <summary>
/// The player's ships by geid, and which ship each locker belongs to.
/// <para>
/// No log line ties a locker to its ship. Its geid does: the game creates a ship's lockers with
/// the ship, a fixed step above the ship's own geid. A Grey's Shiv's <c>-000</c> locker was the
/// ship's geid + 173 on four different Shivs (2026-08 to 2026-11); an Anvil Asgard's
/// <c>PersonalStorage_Template-001</c> was + 348 (2026-11-21). The Asgard's locker names no ship
/// class at all, so the geid is the only way to tell whose it is.
/// </para>
/// <para>
/// A claim gives the ship a new geid and new, empty lockers; what was in the old ones is gone.
/// The log has no line for a claim — searched for claim, insurance, expedite and destroy on
/// 2026-11-22 — so the one sign is a ship of the same class called out of storage under a newer
/// geid. A store and a retrieve keep the geid (2026-10-12), so a retrieval does not count as a
/// replacement of the same ship. Two ships of one class owned at once would read as a claim of
/// the older; the logs cannot tell those cases apart.
/// </para>
/// </summary>
public sealed class ShipFleet
{
    /// <summary>
    /// How far above its ship a locker's geid may sit. The offsets seen are 173 and 348; geids
    /// are handed out by the thousand per spawn, so a much larger gap is some other entity.
    /// </summary>
    public const long MaxLockerOffset = 1000;

    /// <summary>The class name in a locker that names none: <c>PersonalStorage_Template-001</c>.</summary>
    private const string GenericLockerClass = "Template";

    private readonly List<ShipRecord> _byGeid;
    private readonly Dictionary<long, ShipRecord> _index;

    public ShipFleet(IEnumerable<ShipRecord> ships)
    {
        _byGeid = ships.OrderBy(s => s.Geid).ToList();
        _index = _byGeid.ToDictionary(s => s.Geid);
    }

    public static ShipFleet Load(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT geid, class_name, last_seen, retrieved_ts, retrieved_loc_id FROM vehicle";

        var ships = new List<ShipRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!long.TryParse(r.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture, out var geid)) continue;
            ships.Add(new ShipRecord(
                geid,
                r.IsDBNull(1) ? null : r.GetString(1),
                Time(r, 2),
                Time(r, 3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        }
        return new ShipFleet(ships);
    }

    /// <summary>The ship a locker was created with, or null when no known ship sits just below it.</summary>
    /// <param name="lockerGeid">The locker's geid.</param>
    /// <param name="lockerShipClass">The class its name gives, e.g. "GLSN_Shiv" or "Template".</param>
    public ShipRecord? OwnerOf(string lockerGeid, string lockerShipClass)
    {
        if (!long.TryParse(lockerGeid, NumberStyles.None, CultureInfo.InvariantCulture, out var locker)) return null;

        // Walk down from the nearest ship below. A locker that names its class skips ships of
        // another class (one spawned just after its own); a Template locker names nothing to
        // check, so it takes the nearest.
        for (var i = NearestIndex(locker); i >= 0 && locker - _byGeid[i].Geid <= MaxLockerOffset; i--)
        {
            var ship = _byGeid[i];
            if (IsGeneric(lockerShipClass) || ship.ClassName is null
                || ship.ClassName.Equals(lockerShipClass, StringComparison.OrdinalIgnoreCase))
                return ship;
        }
        return null;
    }

    /// <summary>A ship by its geid, as the key <see cref="HoldingResolver"/> gives a ship holding.</summary>
    public ShipRecord? Find(string key) =>
        long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var geid)
            ? _index.GetValueOrDefault(geid)
            : null;

    /// <summary>
    /// The newer ship of the same class that replaced this one — claimed, or destroyed — or null
    /// while it is still the newest of its class the player has called out.
    /// </summary>
    /// <param name="ship">The ship.</param>
    /// <param name="className">Its class, when the ship row lacks one but its locker named it.</param>
    public ShipRecord? ReplacementOf(ShipRecord ship, string? className = null)
    {
        var cls = ship.ClassName ?? className;
        if (cls is null) return null;

        return _byGeid.FirstOrDefault(s =>
            s.Geid > ship.Geid
            && s.RetrievedAt is not null
            && cls.Equals(s.ClassName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a locker's class names no ship: <c>PersonalStorage_Template-001</c>.</summary>
    public static bool IsGeneric(string lockerShipClass) =>
        lockerShipClass.Equals(GenericLockerClass, StringComparison.OrdinalIgnoreCase);

    private int NearestIndex(long locker)
    {
        // The ship with the largest geid below the locker's, or -1.
        int lo = 0, hi = _byGeid.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_byGeid[mid].Geid < locker) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private static DateTimeOffset? Time(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);
}
