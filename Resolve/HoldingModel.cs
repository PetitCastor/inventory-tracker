using LogParser.Model;

namespace LogParser.Resolve;

/// <summary>One recorded relocation, flattened out of the store for in-memory resolution.</summary>
public sealed record MoveRecord(
    long Id,
    DateTimeOffset Timestamp,
    string MoveType,
    string ItemClass,
    string? ItemGeid,
    InventoryKind SourceKind,
    string SourceKey,
    InventoryKind TargetKind,
    string TargetKey,
    string TargetRaw,
    string? Result)
{
    /// <summary>True when the game confirmed the move landed.</summary>
    public bool Succeeded =>
        Result is not null && Result.Equals("succeed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this move names the exact entity. Only Type[Store] does; every other
    /// type reports a class and leaves us guessing which instance moved.
    /// </summary>
    public bool IsInstanceLevel => ItemGeid is not null;
}

/// <summary>One link in the "item -> backpack -> station" chain shown to the user.</summary>
public sealed record HoldingLink(InventoryKind Kind, string Key, string Label);

public enum Confidence { Low, Medium, High }

/// <summary>Where one item instance is believed to be, and how much to trust that.</summary>
public sealed record ItemHolding(
    string Geid,
    string ItemClass,
    IReadOnlyList<HoldingLink> Chain,
    DateTimeOffset LastMove,
    string LastMoveType,
    double Score,
    IReadOnlyList<string> Caveats)
{
    /// <summary>The end of the chain — the station, or the container we lost track of.</summary>
    public HoldingLink? Root => Chain.Count > 0 ? Chain[^1] : null;

    public Confidence Confidence => Score switch
    {
        >= 0.70 => Confidence.High,
        >= 0.40 => Confidence.Medium,
        _ => Confidence.Low,
    };

    /// <summary>Human-readable placement, e.g. "qrt_utility_heavy_backpack -> Stanton1_Lorville".</summary>
    public string Where => Chain.Count == 0
        ? "unknown"
        : string.Join(" → ", Chain.Select(c => c.Label));
}
