using System.Text.RegularExpressions;
using LogParser.Model;

namespace LogParser.Ingest;

/// <summary>
/// Turns a <see cref="LogLine"/> into a typed <see cref="InventoryEvent"/>.
/// Every pattern here was verified against post-4.9 logs; pre-4.9 files use a
/// different event vocabulary entirely and must be excluded before parsing.
/// </summary>
public static partial class InventoryEventParser
{
    /// <summary>4.9 went live on this date. Earlier logs are not parseable by these rules.</summary>
    public static readonly DateTimeOffset Cutoff = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Move types that never change where an item is held: pure reads, and the
    /// re-organise operations whose source and target inventory are the same grid.
    /// </summary>
    public static bool IsNonRelocating(string moveType) =>
        moveType is "QueryInventory" or "OpenNestedInventory" or "Sort" or "StackAll";

    /// <summary>Callers that mean the item went onto the player's body rather than into a grid.</summary>
    private static bool IsAttachCaller(string? caller) =>
        caller is not null && caller.Contains("AttachItem", StringComparison.Ordinal);

    [GeneratedRegex(
        @"^Queued Request\[(?<req>\d+)\] Type\[(?<type>[^\]]*)\] for '(?<player>[^']*)' \[(?<pid>\d+)\] " +
        @"Source Inventory\[(?<srcinv>[^\]]*)\] Target Inventory\[(?<tgtinv>[^\]]*)\]\. " +
        @"Source\[(?<srcitem>[^\]]*)\] amount\[(?<srcamt>\d+)\] rank\[[^\]]*\]\. " +
        @"Target\[(?<tgtitem>[^\]]*)\] amount\[\d+\] rank\[[^\]]*\]\. " +
        @"Item\[(?<item>[^\]]*)\] action\[[^\]]*\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex QueuedRequest();

    [GeneratedRegex(
        @"^New request\[(?<req>\d+)\] Player\[[^\]]*\] Type\[(?<type>[^\]]*)\] " +
        @"SourceInventory\[(?<srcinv>[^\]]*)\] TargetInventory\[[^\]]*\] " +
        @"ItemClass\[(?<class>[^\]]*)\].*?Caller\[(?<caller>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddMove();

    // <Player Inventory Request Complete> Request[N] for 'X' [id] Type[..] Result[Succeed] ...
    [GeneratedRegex(
        @"^Request\[(?<req>\d+)\] for '[^']*' \[\d+\] Type\[[^\]]*\] Result\[(?<result>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlayerRequestComplete();

    // <Inventory Request Completed> Request[N] Player[X] Result[succeed] ...
    [GeneratedRegex(
        @"^Request\[(?<req>\d+)\] Player\[[^\]]*\] Result\[(?<result>[^\]]*)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex RequestCompleted();

    // ... on Inventory[qrt_utility_heavy_backpack_01_01_03_745829874584, 745829874584]
    [GeneratedRegex(
        @"on Inventory\[(?<entity>[A-Za-z0-9_]+), (?<geid>\d{6,})\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenFlow();

    [GeneratedRegex(
        @"Landing \[\d+\] -> \[(?<landing>\d+)\]\. Location \[\d+\] -> \[(?<location>\d+)\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex LocationChange();

    [GeneratedRegex(@"Location\[(?<name>[^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex LocationName();

    public static InventoryEvent? Parse(LogLine line) => line.EventName switch
    {
        "InventoryManagementRequest" => ParseQueued(line),
        "Add Inventory Management Move" => ParseAddMove(line),
        "Player Inventory Request Complete" => ParseComplete(line, PlayerRequestComplete()),
        "Inventory Request Completed" => ParseComplete(line, RequestCompleted()),
        "Inventory Token Flow" => ParseTokenFlow(line),
        "Update Inventory Location" => ParseLocationChange(line),
        "RequestLocationInventory" => ParseLocationName(line),
        _ => null,
    };

    /// <summary>
    /// Applies the caller-dependent normalisations that only make sense once both halves
    /// of a request are known. Returns null when the move does not relocate anything.
    /// </summary>
    public static ItemMoved? Normalize(ItemMoved move, string? caller)
    {
        // Equipping leaves Target Inventory as INVALID; the destination is the player.
        if (move.Target.Kind == InventoryKind.Invalid && IsAttachCaller(caller))
        {
            return move with { Target = InventoryRef.Equipped };
        }

        // Anything else without a real destination tells us nothing about where it ended up.
        if (!move.Target.IsHolding) return null;

        // Same grid in and out: AmmoRepool consolidating magazines, a Split, a re-stack.
        // The item never went anywhere, so recording it would only add noise to the timeline.
        if (move.Source.IsHolding && move.Source.Raw == move.Target.Raw) return null;

        return move;
    }

    private static InventoryEvent? ParseQueued(LogLine line)
    {
        // The same event name also carries "Attempting queuing of..." chatter.
        if (!line.Rest.StartsWith("Queued Request[", StringComparison.Ordinal)) return null;

        var m = QueuedRequest().Match(line.Rest);
        if (!m.Success) return null;

        var moveType = m.Groups["type"].Value;
        if (IsNonRelocating(moveType)) return null;

        // Store names the exact entity; every other type reports only a class in Source[].
        string itemClass;
        string? geid = null;
        if (EntityId.TrySplit(m.Groups["item"].Value, out var cls, out var g))
        {
            itemClass = cls;
            geid = g;
        }
        else
        {
            itemClass = m.Groups["srcitem"].Value;
            if (itemClass is "" or "NULL" or "NONE" or "null") return null;
        }

        return new ItemMoved(
            line.Timestamp,
            int.Parse(m.Groups["req"].Value),
            m.Groups["player"].Value,
            m.Groups["pid"].Value,
            moveType,
            InventoryRef.Parse(m.Groups["srcinv"].Value),
            InventoryRef.Parse(m.Groups["tgtinv"].Value),
            itemClass,
            geid,
            int.Parse(m.Groups["srcamt"].Value));
    }

    private static InventoryEvent? ParseAddMove(LogLine line)
    {
        var m = AddMove().Match(line.Rest);
        if (!m.Success) return null;

        var moveType = m.Groups["type"].Value;
        if (IsNonRelocating(moveType)) return null;

        return new MoveRequested(
            line.Timestamp,
            int.Parse(m.Groups["req"].Value),
            moveType,
            InventoryRef.Parse(m.Groups["srcinv"].Value),
            m.Groups["class"].Value,
            m.Groups["caller"].Value);
    }

    private static InventoryEvent? ParseComplete(LogLine line, Regex pattern)
    {
        var m = pattern.Match(line.Rest);
        if (!m.Success) return null;

        return new MoveCompleted(
            line.Timestamp,
            int.Parse(m.Groups["req"].Value),
            m.Groups["result"].Value);
    }

    private static InventoryEvent? ParseTokenFlow(LogLine line)
    {
        var m = TokenFlow().Match(line.Rest);
        if (!m.Success) return null;

        var geid = m.Groups["geid"].Value;

        // The entity is "<class>_<geid>"; the trailing geid must agree with the standalone one.
        if (!EntityId.TrySplit(m.Groups["entity"].Value, out var cls, out var entityGeid)) return null;
        if (entityGeid != geid) return null;

        return new ContainerIdentified(line.Timestamp, geid, cls);
    }

    private static InventoryEvent? ParseLocationChange(LogLine line)
    {
        var m = LocationChange().Match(line.Rest);
        return m.Success
            ? new LocationChanged(line.Timestamp, m.Groups["landing"].Value, m.Groups["location"].Value)
            : null;
    }

    private static InventoryEvent? ParseLocationName(LogLine line)
    {
        var m = LocationName().Match(line.Rest);
        return m.Success ? new LocationNamed(line.Timestamp, m.Groups["name"].Value) : null;
    }
}
