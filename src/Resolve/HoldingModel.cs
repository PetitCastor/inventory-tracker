using InventoryTracker.Model;

namespace InventoryTracker.Resolve;

/// <summary>One recorded relocation, flattened out of the store for in-memory resolution.</summary>
public sealed record MoveRecord(
    long Id,
    DateTimeOffset Timestamp,
    string MoveType,
    string ItemClass,
    string? ItemGeid,
    int Amount,
    InventoryKind SourceKind,
    string SourceKey,
    InventoryKind TargetKind,
    string TargetKey,
    string TargetRaw,
    string? Result)
{
    /// <summary>False only when the game explicitly said the move failed.</summary>
    public bool Failed =>
        Result is not null && Result.Equals("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the game confirmed the move landed.</summary>
    public bool Succeeded =>
        Result is not null && Result.Equals("succeed", StringComparison.OrdinalIgnoreCase);

    /// <summary>How many units moved. Only a stacked Type[Move] ever carries more than one.</summary>
    public int Units => ItemGeid is not null ? 1 : Math.Max(Amount, 1);
}

/// <summary>Somewhere an item can sit: a station's local inventory, a container, the player, the ground.</summary>
public readonly record struct Holding(InventoryKind Kind, string Key)
{
    public bool IsReal => Kind is InventoryKind.Location or InventoryKind.Container
                              or InventoryKind.Equipped or InventoryKind.World;
}

/// <summary>One link in the "item -> backpack -> station" chain shown to the user.</summary>
public sealed record HoldingLink(InventoryKind Kind, string Key, string Label);

public enum Confidence { Low, Medium, High }

/// <summary>
/// A quantity of one item class believed to be sitting in one place, and how much to trust
/// that.
/// <para>
/// Rows come in two flavours. A named row tracks one entity the log identified by geid. An
/// anonymous row is a count the log only ever described by class — most magazines, drinks
/// and consumables are moved this way, and the game never says which of several identical
/// items was picked up. Anonymity limits what can be said about <i>which</i> item this is,
/// not about whether something of this class is here, so it does not lower the score.
/// </para>
/// </summary>
public sealed record ItemHolding(
    string? Geid,
    string ItemClass,
    int Quantity,
    IReadOnlyList<HoldingLink> Chain,
    DateTimeOffset LastMove,
    string LastMoveType,
    double Score,
    bool IdentityAmbiguous,
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

    /// <summary>Human-readable placement, e.g. "qrt_utility_heavy_backpack -> Stanton Gateway".</summary>
    public string Where => Chain.Count == 0
        ? "unknown"
        : string.Join(" → ", Chain.Select(c => c.Label));
}
