using System.Globalization;
using InventoryTracker.Data;
using InventoryTracker.Model;
using InventoryTracker.Naming;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Resolve;

/// <summary>
/// Turns the recorded move history into "what is sitting where", by replaying it.
/// <para>
/// There is no snapshot event in the logs — nothing ever enumerates an inventory's
/// contents — so every answer here is inference from a stream of deltas over an unknown
/// starting state. That has hard limits worth stating plainly: an item bought, looted or
/// claimed without a recorded move is invisible, and anything the server changes on its own
/// (a death, an insurance claim, a wipe, another player emptying a shared container) is
/// never logged at all. What this produces is a well-evidenced lower bound, not a manifest,
/// and every row carries a score and its caveats rather than being stated as fact.
/// </para>
/// </summary>
public sealed class HoldingResolver
{
    /// <summary>Containers nested deeper than this are a data error, not a real loadout.</summary>
    private const int MaxChainDepth = 6;

    /// <summary>Past this age a placement is old enough that the world has probably moved on.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>A container's class plus the last place we saw the player open it. The class
    /// is null for a container the game never named — its capacity in µSCU, if a drag line
    /// ever reported one, is then all there is to describe it by.</summary>
    public sealed record ContainerInfo(
        string? ClassName, string? LastLocationId, long? Capacity);

    private readonly List<MoveRecord> _moves;
    private readonly Dictionary<string, string> _locationNames;
    private readonly Dictionary<string, ContainerInfo> _containers;

    /// <summary>Geids of containers ever seen worn on the body (backpack, legs, chest,
    /// undersuit). These are apparel that travels with the player, so they aggregate an item's
    /// moves but are never shown as a container — the chain skips straight through them.</summary>
    private readonly HashSet<string> _wornContainers;

    private readonly Dictionary<string, List<MoveRecord>> _byGeid;

    /// <summary>Per item class, the lifespan (first and last move) of every named entity of
    /// that class. Used to spot a stored instance that a later, never-overlapping entity of
    /// the same class superseded — the fingerprint of a relog re-issuing the geid.</summary>
    private readonly Dictionary<string, List<(string Geid, DateTimeOffset First, DateTimeOffset Last)>> _classSpans;

    private readonly LedgerReplay _ledger;

    /// <summary>Systems and place names as a player would recognise them.</summary>
    public PlaceCatalog Places { get; }

    public IReadOnlyList<MoveRecord> Moves => _moves;

    private HoldingResolver(
        List<MoveRecord> moves,
        List<LedgerReplay.WornSighting> worn,
        Dictionary<string, string> locationNames,
        Dictionary<string, ContainerInfo> containers,
        HashSet<string> wornContainers,
        PlaceCatalog places)
    {
        _moves = moves;
        _locationNames = locationNames;
        _containers = containers;
        _wornContainers = wornContainers;
        Places = places;

        _byGeid = moves
            .Where(m => m.ItemGeid is not null)
            .GroupBy(m => m.ItemGeid!)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Timestamp).ToList());

        _classSpans = _byGeid.Values
            .Select(h => (Geid: h[^1].ItemGeid!, Class: h[^1].ItemClass, First: h[0].Timestamp, Last: h[^1].Timestamp))
            .GroupBy(x => x.Class)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.Geid, x.First, x.Last)).ToList());

        // Container class is only known once its Token Flow line has been seen, which can be
        // anywhere in the log relative to a move that targets it — so this can only be
        // resolved here, after ingestion, with the full catalogue in hand.
        var backpackKeys = containers
            .Where(kv => IsBackpack(kv.Value.ClassName))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        _ledger = LedgerReplay.Run(moves, worn, backpackKeys);
    }

    /// <summary>The whole move history is only a few thousand rows, so resolve in memory.</summary>
    public static HoldingResolver Load(TrackerDb db)
    {
        using var cn = db.Open();
        return new HoldingResolver(
            LoadMoves(cn), LoadWorn(cn), LoadLocationNames(cn), LoadContainers(cn),
            LoadWornContainers(cn), PlaceCatalog.Load(db));
    }

    /// <summary>
    /// Everything the ledger believes is currently held, one row per (place, item class):
    /// named entities individually, and unnamed units as a count.
    /// </summary>
    public IReadOnlyList<ItemHolding> ResolveAll()
    {
        var rows = new List<ItemHolding>();

        foreach (var where in _ledger.Occupied)
        {
            foreach (var (itemClass, named, loose) in _ledger.Contents(where))
            {
                foreach (var instance in named)
                {
                    rows.Add(Build(
                        where, itemClass, instance.Geid, quantity: 1, instance.Since, instance.ArrivedBy,
                        instance.Confirmed, instance.Inferred, instance.IdentityAmbiguous));
                }

                if (loose is { Quantity: > 0 })
                {
                    rows.Add(Build(
                        where, itemClass, geid: null, loose.Quantity, loose.Since, loose.ArrivedBy,
                        loose.Confirmed, loose.Inferred, ambiguous: true));
                }
            }
        }

        return rows.OrderByDescending(h => h.LastMove).ToList();
    }

    /// <summary>Where one named entity ended up, or null if it was never seen.</summary>
    public ItemHolding? Resolve(string geid)
    {
        if (!_ledger.InstanceAt.TryGetValue(geid, out var where)) return null;

        var instance = _ledger.NamedIn(where, ClassOf(geid)).FirstOrDefault(i => i.Geid == geid);
        if (instance is null) return null;

        return Build(
            where, instance.ItemClass, geid, quantity: 1, instance.Since, instance.ArrivedBy,
            instance.Confirmed, instance.Inferred, instance.IdentityAmbiguous);
    }

    private string ClassOf(string geid) =>
        _byGeid.TryGetValue(geid, out var h) && h.Count > 0 ? h[^1].ItemClass : "";

    /// <summary>
    /// True when another entity of the same class first appeared strictly after this one's
    /// last move — a later identity that never coexisted with it. Two items held at once
    /// overlap in time and are left alone; only a clean hand-off (this one goes quiet, a new
    /// geid takes over) reads as the same physical item relogged under a fresh id.
    /// </summary>
    private bool SupersededByRelog(string geid, string itemClass)
    {
        if (!_classSpans.TryGetValue(itemClass, out var spans)) return false;

        var mine = spans.FirstOrDefault(s => s.Geid == geid);
        if (mine.Geid is null) return false;

        return spans.Any(other => other.Geid != geid && other.First > mine.Last);
    }

    /// <summary>Full movement history for one instance, oldest first — the audit view.</summary>
    public IReadOnlyList<MoveRecord> History(string geid) =>
        _byGeid.TryGetValue(geid, out var h) ? h : [];

    private ItemHolding Build(
        Holding where, string itemClass, string? geid, int quantity, DateTimeOffset since,
        string arrivedBy, bool confirmed, bool inferred, bool ambiguous)
    {
        var caveats = new List<string>();
        var score = 1.0;

        if (!confirmed)
        {
            score *= 0.5;
            caveats.Add("the game never confirmed the move that put it here");
        }

        if (inferred)
        {
            score *= 0.8;
            caveats.Add("we saw this arrive but never saw where it came from, so it may have been counted twice");
        }

        if (arrivedBy == "Drop")
        {
            score *= 0.3;
            caveats.Add("dropped as a loose world entity — these despawn");
        }

        var age = DateTimeOffset.UtcNow - since;
        if (age > StaleAfter)
        {
            score *= 0.8;
            caveats.Add($"last seen {age.TotalDays:F0} days ago");
        }

        // A named item parked at a station, whose class then turns up under a different entity
        // that only ever existed after this one went quiet, is almost certainly the same
        // physical item picked back up: the game re-issues an entity id every session, so the
        // pickup was logged under a new geid that can never be tied back to this row. Its
        // stored position is therefore stale — knock it well down rather than assert it.
        if (geid is not null && where.Kind == InventoryKind.Location && SupersededByRelog(geid, itemClass))
        {
            score *= 0.35;
            caveats.Add(
                "the same kind of item was carried later under a different identity that never "
                + "overlapped this one — entities are re-issued each session, so this was most "
                + "likely picked back up and this location is stale");
        }

        // Which of several identical items this is says nothing about whether one of them
        // is here, so it is reported without touching the score.
        if (ambiguous && geid is not null)
        {
            caveats.Add("the log did not name which of several identical items was moved, so this instance may be one of its twins");
        }

        var chain = BuildChain(where, geid, caveats, ref score);

        return new ItemHolding(
            geid, itemClass, quantity, chain, since, arrivedBy, score, ambiguous, caveats);
    }

    /// <summary>
    /// Walks from where the item sits outward: an item is in a backpack, and the backpack is
    /// itself an entity whose own last move says which station it is at.
    /// </summary>
    private List<HoldingLink> BuildChain(
        Holding start, string? itemGeid, List<string> caveats, ref double score)
    {
        var chain = new List<HoldingLink>();
        var seen = new HashSet<string>();
        if (itemGeid is not null) seen.Add(itemGeid);

        var kind = start.Kind;
        var key = start.Key;

        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            switch (kind)
            {
                case InventoryKind.Location:
                {
                    var place = Places.ByLocationId(key);
                    if (place is not null)
                    {
                        chain.Add(new HoldingLink(kind, key, place.Name));
                    }
                    else if (_locationNames.TryGetValue(key, out var raw))
                    {
                        chain.Add(new HoldingLink(kind, key, raw));
                    }
                    else
                    {
                        score *= 0.7;
                        caveats.Add("this station's id was never paired with a name");
                        chain.Add(new HoldingLink(kind, key, $"location {key}"));
                    }
                    return chain;
                }

                case InventoryKind.Equipped:
                    chain.Add(new HoldingLink(kind, "", "equipped on your character"));
                    return chain;

                case InventoryKind.World:
                    chain.Add(new HoldingLink(kind, "", "dropped in the world"));
                    return chain;

                case InventoryKind.Container:
                {
                    var info = _containers.GetValueOrDefault(key);
                    var label = info?.ClassName ?? SizeLabel(info?.Capacity, key);
                    var lootable = IsLootable(info?.ClassName);

                    // Worn apparel (backpack, chest, legs, arms, undersuit) is not a place — it
                    // travels with the player. Its contents aggregate an item's moves, but it
                    // is never shown as a container: emit no link and walk straight through it
                    // to wherever the apparel itself ended up. It is recognised by class name —
                    // the reliable signal, since apparel is used for nested storage far more
                    // often than it is seen worn — with an attachment sighting as a backstop.
                    var worn = IsApparel(info?.ClassName) || _wornContainers.Contains(key);
                    if (!worn) chain.Add(new HoldingLink(kind, key, label));

                    // A container that contains itself would loop forever.
                    if (!seen.Add(key))
                    {
                        caveats.Add("container nesting loops back on itself");
                        return chain;
                    }

                    // Every extra hop is another inference stacked on the last one. A skipped
                    // worn hop is not a shown inference, and an attachment sighting is
                    // authoritative, so it costs nothing.
                    if (depth > 0 && !worn) score *= 0.9;

                    // The container is an entity too, so the ledger knows where it went.
                    if (_ledger.InstanceAt.TryGetValue(key, out var holdingTheContainer))
                    {
                        kind = holdingTheContainer.Kind;
                        key = holdingTheContainer.Key;
                        continue;
                    }

                    // Boxes and crates get set down rather than stored, so they often have
                    // no move history at all. Opening one still proves it was within arm's
                    // reach, which pins it to wherever the player was standing — but a
                    // lootable container is found, not owned, and is exactly as likely to
                    // be opened out in the field as at a real station. Pinning those spawns
                    // a "place" for every wreck and crate ever looted, most never named by
                    // the game, which is what fills the browse list with "Unknown system".
                    // Lootable containers are therefore never tracked as a place at all.
                    if (!lootable && info?.LastLocationId is { } seenAt)
                    {
                        score *= 0.8;
                        caveats.Add($"'{label}' was placed by where you last opened it, not by a recorded move");
                        kind = InventoryKind.Location;
                        key = seenAt;
                        continue;
                    }

                    // A worn container we never pinned leaves the item with no place at all,
                    // rather than the bare backpack it used to show. Say so instead of dropping
                    // it silently.
                    if (worn)
                    {
                        score *= 0.6;
                        caveats.Add("worn apparel whose current location was never recorded");
                        return chain;
                    }

                    score *= 0.6;
                    caveats.Add(lootable
                        ? $"'{label}' is a lootable container, not a tracked place"
                        : $"we have never seen where '{label}' itself is kept");
                    return chain;
                }

                default:
                    return chain;
            }
        }

        caveats.Add("container nesting was deeper than we follow");
        return chain;
    }

    private static List<MoveRecord> LoadMoves(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, move_type, item_class, item_geid, amount,
                   src_kind, src_key, tgt_kind, tgt_key, tgt_raw, result
            FROM move
            ORDER BY ts, id
            """;

        var rows = new List<MoveRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new MoveRecord(
                r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5),
                ParseKind(r.GetString(6)),
                r.GetString(7),
                ParseKind(r.GetString(8)),
                r.GetString(9),
                r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11)));
        }
        return rows;
    }

    /// <summary>
    /// How much of the newest spawn counts as one enumeration. Attachment lines are emitted
    /// in a burst as the character streams in.
    /// </summary>
    private static readonly TimeSpan WornBurst = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The ports worn at the most recent spawn.
    /// <para>
    /// Each spawn enumerates the whole body, so an older sighting is not just stale — it is
    /// contradicted by a later enumeration that left the item out. Those are dropped rather
    /// than believed, which leaves the item wherever its moves last put it instead of
    /// claiming it is still being worn months later.
    /// </para>
    /// </summary>
    private static List<LedgerReplay.WornSighting> LoadWorn(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT geid, class_name, last_seen FROM attachment";

        var all = new List<LedgerReplay.WornSighting>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                all.Add(new LedgerReplay.WornSighting(
                    DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                    r.GetString(0),
                    r.GetString(1)));
            }
        }

        if (all.Count == 0) return all;

        var newest = all.Max(w => w.Timestamp);
        return all.Where(w => newest - w.Timestamp <= WornBurst).ToList();
    }

    /// <summary>
    /// Whether a container class is body-worn apparel — a backpack or an armour slot (core for
    /// the chest, legs, arms) or the undersuit. These carry storage but are worn on the
    /// character, so they aggregate an item's moves without ever being shown as a place. The
    /// world's own containers (freight elevators, lootable crates, carryable inventory boxes)
    /// match none of these and keep their chain hop.
    /// </summary>
    private static bool IsApparel(string? className)
    {
        if (className is null) return false;

        foreach (var mark in ApparelMarks)
        {
            if (className.Contains(mark, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly string[] ApparelMarks =
        ["backpack", "undersuit", "_core_", "_legs_", "_arms_"];

    /// <summary>One SCU as the logs count it. Capacities are written in µSCU.</summary>
    private const double MicroScuPerScu = 1_000_000;

    /// <summary>
    /// What to call a container the game never named. Its capacity is the one thing ever
    /// said about it, and it is also how the player thinks of the thing they set down —
    /// a 2 SCU box. Without even that, the geid is all that distinguishes it.
    /// </summary>
    private static string SizeLabel(long? capacity, string key)
    {
        if (capacity is not > 0) return $"container {key}";

        var scu = capacity.Value / MicroScuPerScu;
        return scu >= 1
            ? $"{scu:0.##} SCU container"
            : $"{capacity.Value / (MicroScuPerScu / 100):0.##} cSCU container";
    }

    /// <summary>
    /// Whether a container class is one of the game's found-loot crates (class names like
    /// <c>Lootable_Container_...</c>) rather than storage the player actually owns. These are
    /// scattered through the world and opened once in passing, so unlike a carryable box or a
    /// freight elevator, their position is not worth remembering as a place.
    /// </summary>
    private static bool IsLootable(string? className) =>
        className is not null && className.Contains("lootable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whatever put an item in the backpack — dragged from a location, another container,
    /// or straight out of hand — says nothing about where the player actually is, so it is
    /// never worth recording as a placement. Recognised by class name, the same reliable
    /// signal <see cref="IsApparel"/> uses.
    /// </summary>
    private static bool IsBackpack(string? className) =>
        className is not null && className.Contains("backpack", StringComparison.OrdinalIgnoreCase);

    private static InventoryKind ParseKind(string s) =>
        Enum.TryParse<InventoryKind>(s, out var k) ? k : InventoryKind.Unknown;

    private static Dictionary<string, string> LoadLocationNames(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT location_id, name FROM location_name";

        var map = new Dictionary<string, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    private static Dictionary<string, ContainerInfo> LoadContainers(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT geid, class_name, last_loc_id, capacity FROM container_class";

        var map = new Dictionary<string, ContainerInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = new ContainerInfo(
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt64(3));
        }
        return map;
    }

    /// <summary>
    /// Every container geid ever enumerated on the player's body. Unlike <see cref="LoadWorn"/>
    /// this is the full history, not the recent burst: a backpack worn last week and stored
    /// today is still apparel and must never be shown as a container. Membership in the
    /// attachment table is the signal — world crates and freight elevators never appear there.
    /// </summary>
    private static HashSet<string> LoadWornContainers(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT geid FROM attachment WHERE port IS NOT NULL";

        var set = new HashSet<string>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }
}
