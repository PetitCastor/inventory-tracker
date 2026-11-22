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
    string? Result,
    string? Action = null,
    decimal? Price = null)
{
    /// <summary>False only when the game explicitly said the move failed.</summary>
    public bool Failed =>
        Result is not null && Result.Equals("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the server never processed the request before the connection closed: the
    /// move did not happen.
    /// </summary>
    public bool Lost =>
        Result is not null && Result.Equals(Ingest.LogIngestor.LostResult, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the game confirmed the move landed.</summary>
    public bool Succeeded =>
        Result is not null && Result.Equals("succeed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to call the way this move delivered its item. A carry is an Interaction like an
    /// equip, but what happens next is very different — a drink is drunk, not worn — so it is
    /// told apart for scoring.
    /// </summary>
    public string ArrivedBy => Action == Ingest.InventoryEventParser.CarryAction ? "Carry" : MoveType;

    /// <summary>How many units moved. Only a stacked Type[Move] ever carries more than one.</summary>
    public int Units => ItemGeid is not null ? 1 : Math.Max(Amount, 1);
}

/// <summary>Somewhere an item can sit: a station's local inventory, a container, the player, the ground.</summary>
public readonly record struct Holding(InventoryKind Kind, string Key)
{
    public bool IsReal => Kind is InventoryKind.Location or InventoryKind.Container
                              or InventoryKind.Equipped or InventoryKind.World or InventoryKind.Corpse;
}

/// <summary>One link in the "item -> backpack -> station" chain shown to the user.</summary>
public sealed record HoldingLink(InventoryKind Kind, string Key, string Label)
{
    /// <summary>
    /// The game class this link names, when <see cref="Label"/> is built from one (a box, or the
    /// rifle a scope is mounted on). The resolver has no item catalogue, so it labels these with
    /// the raw class; <see cref="Named"/> lets the UI swap in the name the item column shows.
    /// </summary>
    public string? ItemClass { get; init; }

    /// <summary>Text before the class in <see cref="Label"/>, such as "on ".</summary>
    public string LabelPrefix { get; init; } = "";

    /// <summary>The label with <see cref="ItemClass"/> replaced by <paramref name="displayName"/>'s name for it.</summary>
    public string Named(Func<string, string> displayName) =>
        ItemClass is { } itemClass ? LabelPrefix + displayName(itemClass) : Label;
}

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
    /// <summary>
    /// What the item sits in directly. Unlike <see cref="Chain"/>, which walks through worn
    /// apparel without a link, this tells an item in a backpack from the backpack itself.
    /// </summary>
    public Holding Holder { get; init; }

    /// <summary>How the item came to the player, as far as the logs say. Shown as a tag.</summary>
    public ItemOrigin Origin { get; init; }

    /// <summary>The end of the chain — the station, or the container we lost track of.</summary>
    public HoldingLink? Root => Chain.Count > 0 ? Chain[^1] : null;

    /// <summary>
    /// True when the item was put away at a station, directly or inside something stored there.
    /// The tracker exists to find what the player deliberately stored; what is worn, carried,
    /// dropped or of unknown whereabouts is not shown, in the station view or in search.
    /// </summary>
    public bool IsAtStation => Root is { Kind: InventoryKind.Location };

    /// <summary>
    /// True when the item was dropped on the floor, loose or inside something dropped. These
    /// get their own page: a drop is as often a mistake as a choice, and the game despawns
    /// what is left lying anywhere but a hangar.
    /// </summary>
    public bool IsDropped => Root is { Kind: InventoryKind.World };

    /// <summary>
    /// True when the item was on the player when they died and is still on the body, loose or
    /// inside a backpack or armour. Not all of it is lost: the respawn gives the worn gear back
    /// (see <see cref="HoldingResolver.IsLostAtDeath"/>).
    /// </summary>
    public bool IsLostAtDeath => Root is { Kind: InventoryKind.Corpse };

    /// <summary>True when the item is in one of a ship's lockers, however deep.</summary>
    public bool IsInShip => Root is { Kind: InventoryKind.Ship };

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

    /// <summary><see cref="Where"/> with item classes replaced by player-facing names.</summary>
    public string WhereNamed(Func<string, string> displayName) => Chain.Count == 0
        ? "unknown"
        : string.Join(" → ", Chain.Select(c => c.Named(displayName)));
}
