using LogParser.Data;
using LogParser.Model;
using Microsoft.Data.Sqlite;

namespace LogParser.Resolve;

/// <summary>
/// Infers where each item instance currently is by replaying its move history.
/// <para>
/// There is no snapshot event in the logs — nothing ever enumerates an inventory's
/// contents — so every answer here is an inference from the last recorded move, and
/// each one carries a score and an explicit list of caveats rather than being stated
/// as fact.
/// </para>
/// </summary>
public sealed class HoldingResolver
{
    /// <summary>Containers nested deeper than this are a data error, not a real loadout.</summary>
    private const int MaxChainDepth = 6;

    /// <summary>Past this age a placement is old enough that the world has probably moved on.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>A container's class plus the last place we saw the player open it.</summary>
    public sealed record ContainerInfo(string ClassName, string? LastLocationId, DateTimeOffset? LastSeenAt);

    private readonly List<MoveRecord> _moves;
    private readonly Dictionary<string, string> _locationNames;
    private readonly Dictionary<string, ContainerInfo> _containers;

    /// <summary>Instance-level moves per geid, oldest first.</summary>
    private readonly Dictionary<string, List<MoveRecord>> _byGeid;

    /// <summary>Class-level moves per class, oldest first. Used to detect ambiguity.</summary>
    private readonly Dictionary<string, List<MoveRecord>> _classMovesByClass;

    private HoldingResolver(
        List<MoveRecord> moves,
        Dictionary<string, string> locationNames,
        Dictionary<string, ContainerInfo> containers)
    {
        _moves = moves;
        _locationNames = locationNames;
        _containers = containers;

        _byGeid = moves
            .Where(m => m.ItemGeid is not null)
            .GroupBy(m => m.ItemGeid!)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Timestamp).ToList());

        _classMovesByClass = moves
            .Where(m => m.ItemGeid is null)
            .GroupBy(m => m.ItemClass)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Timestamp).ToList());
    }

    /// <summary>The whole move history is only a few thousand rows, so resolve in memory.</summary>
    public static HoldingResolver Load(TrackerDb db)
    {
        using var cn = db.Open();
        return new HoldingResolver(LoadMoves(cn), LoadLocationNames(cn), LoadContainers(cn));
    }

    public IReadOnlyList<MoveRecord> Moves => _moves;

    /// <summary>
    /// Every item instance we have ever seen named, with its inferred placement.
    /// <para>
    /// Resolution runs twice: the first pass places everything, and the second uses those
    /// placements to judge ambiguity. A class-level departure only casts real doubt on an
    /// item if few identical items were sitting there — one bottle leaving a crate of
    /// twelve barely implicates any particular bottle.
    /// </para>
    /// </summary>
    public IReadOnlyList<ItemHolding> ResolveAll()
    {
        var placed = _byGeid.Keys
            .Select(g => Resolve(g))
            .OfType<ItemHolding>()
            .ToList();

        var occupancy = placed
            .Where(h => h.Root is not null)
            .GroupBy(h => (h.ItemClass, h.Chain[0].Kind, h.Chain[0].Key))
            .ToDictionary(g => g.Key, g => g.Count());

        return placed
            .Select(h => ApplyAmbiguity(h, occupancy))
            .OrderByDescending(h => h.LastMove)
            .ToList();
    }

    private ItemHolding ApplyAmbiguity(
        ItemHolding holding,
        Dictionary<(string, InventoryKind, string), int> occupancy)
    {
        if (holding.Chain.Count == 0) return holding;

        var here = holding.Chain[0];
        var departures = CountDepartures(holding.ItemClass, here, holding.LastMove);
        if (departures == 0) return holding;

        var siblings = occupancy.GetValueOrDefault((holding.ItemClass, here.Kind, here.Key), 1);
        var chanceItLeft = Math.Min(1.0, (double)departures / siblings);

        var caveats = holding.Caveats.ToList();
        caveats.Add(siblings > 1
            ? $"{departures} of {siblings} '{holding.ItemClass}' here were moved out afterwards, and the log does not say which"
            : $"a '{holding.ItemClass}' was moved out of here afterwards");

        return holding with
        {
            Score = holding.Score * (1 - 0.5 * chanceItLeft),
            Caveats = caveats,
        };
    }

    public ItemHolding? Resolve(string geid)
    {
        if (!_byGeid.TryGetValue(geid, out var history) || history.Count == 0) return null;

        var last = history[^1];
        var caveats = new List<string>();
        var score = 1.0;

        if (!last.Succeeded)
        {
            score *= 0.5;
            caveats.Add("the game never confirmed this move succeeded");
        }

        if (last.MoveType == "Drop")
        {
            score *= 0.3;
            caveats.Add("dropped as a loose world entity — these despawn");
        }

        var age = DateTimeOffset.UtcNow - last.Timestamp;
        if (age > StaleAfter)
        {
            score *= 0.8;
            caveats.Add($"last seen {age.TotalDays:F0} days ago");
        }

        var chain = BuildChain(last, geid, caveats, ref score);

        return new ItemHolding(
            geid, last.ItemClass, chain, last.Timestamp, last.MoveType, score, caveats);
    }

    /// <summary>Full movement history for one instance, oldest first — the audit view.</summary>
    public IReadOnlyList<MoveRecord> History(string geid) =>
        _byGeid.TryGetValue(geid, out var h) ? h : [];

    /// <summary>
    /// Walks from the item's destination outward: an item is in a backpack, and the
    /// backpack is itself an item whose own last move says which station it is at.
    /// </summary>
    private List<HoldingLink> BuildChain(
        MoveRecord last, string itemGeid, List<string> caveats, ref double score)
    {
        var chain = new List<HoldingLink>();
        var seen = new HashSet<string> { itemGeid };

        var kind = last.TargetKind;
        var key = last.TargetKey;

        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            switch (kind)
            {
                case InventoryKind.Location:
                    if (_locationNames.TryGetValue(key, out var name))
                    {
                        chain.Add(new HoldingLink(kind, key, name));
                    }
                    else
                    {
                        score *= 0.7;
                        caveats.Add("this station's id was never paired with a name");
                        chain.Add(new HoldingLink(kind, key, $"location {key}"));
                    }
                    return chain;

                case InventoryKind.Equipped:
                    chain.Add(new HoldingLink(kind, "", "equipped on your character"));
                    return chain;

                case InventoryKind.World:
                    chain.Add(new HoldingLink(kind, "", "dropped in the world"));
                    return chain;

                case InventoryKind.Container:
                {
                    var info = _containers.GetValueOrDefault(key);
                    var label = info?.ClassName ?? $"container {key}";
                    chain.Add(new HoldingLink(kind, key, label));

                    // A container that contains itself would loop forever.
                    if (!seen.Add(key))
                    {
                        caveats.Add("container nesting loops back on itself");
                        return chain;
                    }

                    // Every extra hop is another inference stacked on the last one.
                    if (depth > 0) score *= 0.9;

                    // The container is an item too, so its own last move places it.
                    if (_byGeid.TryGetValue(key, out var h) && h.Count > 0)
                    {
                        kind = h[^1].TargetKind;
                        key = h[^1].TargetKey;
                        continue;
                    }

                    // Boxes and crates get set down rather than stored, so they often have
                    // no move history at all. Opening one still proves it was within arm's
                    // reach, which pins it to wherever the player was standing.
                    if (info?.LastLocationId is { } seenAt)
                    {
                        score *= 0.8;
                        caveats.Add($"'{label}' was placed by where you last opened it, not by a recorded move");
                        kind = InventoryKind.Location;
                        key = seenAt;
                        continue;
                    }

                    score *= 0.6;
                    caveats.Add($"we have never seen where '{label}' itself is kept");
                    return chain;
                }

                default:
                    return chain;
            }
        }

        caveats.Add("container nesting was deeper than we follow");
        return chain;
    }

    /// <summary>
    /// How many class-level moves took an item of this class out of <paramref name="holding"/>
    /// after <paramref name="after"/>. Those moves never name a geid, so we cannot tell
    /// whether the item that left was this one.
    /// </summary>
    private int CountDepartures(string itemClass, HoldingLink holding, DateTimeOffset after)
    {
        if (!_classMovesByClass.TryGetValue(itemClass, out var moves)) return 0;

        return moves.Count(m =>
            m.Timestamp > after &&
            m.SourceKind == holding.Kind &&
            m.SourceKey == holding.Key);
    }

    private static List<MoveRecord> LoadMoves(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, move_type, item_class, item_geid,
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
                DateTimeOffset.Parse(r.GetString(1)),
                r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                ParseKind(r.GetString(5)),
                r.GetString(6),
                ParseKind(r.GetString(7)),
                r.GetString(8),
                r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return rows;
    }

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
        cmd.CommandText = "SELECT geid, class_name, last_loc_id, last_loc_ts FROM container_class";

        var map = new Dictionary<string, ContainerInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = new ContainerInfo(
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)));
        }
        return map;
    }
}
