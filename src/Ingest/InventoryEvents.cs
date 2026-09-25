using InventoryTracker.Model;

namespace InventoryTracker.Ingest;

/// <summary>Marker for everything <see cref="InventoryEventParser"/> can emit.</summary>
public abstract record InventoryEvent(DateTimeOffset Timestamp);

/// <summary>
/// The authoritative move record, from
/// <c>&lt;InventoryManagementRequest&gt; Queued Request[N] ...</c>.
/// <para>
/// The line has two shapes and they carry different precision:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Store</b> — instance-level. <c>Item[&lt;class&gt;_&lt;geid&gt;]</c> names the exact
/// entity, <c>Target Inventory</c> is the destination, <c>Source Inventory</c> is INVALID.
/// </item>
/// <item>
/// <b>Move / Interaction / Split / Stack</b> — class-level. <c>Item[NONE]</c>; the class
/// comes from <c>Source[&lt;class&gt;] amount[N]</c> and <i>both</i> inventory fields are
/// meaningful and correctly oriented. <see cref="ItemGeid"/> is null: the log does not say
/// which of several identical items moved.
/// </item>
/// </list>
/// </summary>
public sealed record ItemMoved(
    DateTimeOffset Timestamp,
    int RequestNo,
    string Player,
    string PlayerId,
    string MoveType,
    InventoryRef Source,
    InventoryRef Target,
    string ItemClass,
    string? ItemGeid,
    int Amount,
    string? Action = null) : InventoryEvent(Timestamp);

/// <summary>
/// <c>&lt;Add Inventory Management Move&gt; New request[N] ...</c>.
/// <para>
/// Mostly a companion to <see cref="ItemMoved"/>, supplying the <c>Caller[...]</c> that
/// distinguishes an equip from a plain grid drag. Two cautions:
/// </para>
/// <list type="bullet">
/// <item>For Type[Store] its Source/Target inventories are <i>inverted</i> relative to the
/// Queued line, so they must never be merged.</item>
/// <item>Type[Drop] emits <i>only</i> this line — no Queued counterpart — so it is the sole
/// record of an item being thrown on the floor. There the inventories are oriented correctly.</item>
/// </list>
/// </summary>
public sealed record MoveRequested(
    DateTimeOffset Timestamp,
    int RequestNo,
    string MoveType,
    InventoryRef SourceInventory,
    string ItemClass,
    string Caller) : InventoryEvent(Timestamp);

/// <summary>
/// A single <c>&lt;Add Inventory Management Move&gt;</c> that carries a bracketed list of
/// classes — <c>ItemClass[[a] [b] [c] ]</c> — instead of one class.
/// <para>
/// These are the only record of a multi-select drag. The paired Queued line degenerates to
/// <c>Source[NULL] ... Item[NONE]</c> and so says nothing, which is why the batch has to be
/// reconstructed from here. Unlike Type[Store], the inventories on this line are oriented
/// correctly, so a batch is only trusted for the non-Store types it is actually observed on.
/// </para>
/// </summary>
public sealed record MoveBatchRequested(
    DateTimeOffset Timestamp,
    int RequestNo,
    string MoveType,
    InventoryRef Source,
    InventoryRef Target,
    IReadOnlyList<string> ItemClasses,
    string Caller) : InventoryEvent(Timestamp);

/// <summary>Outcome for a request, from either of the two "complete" events.</summary>
public sealed record MoveCompleted(
    DateTimeOffset Timestamp,
    int RequestNo,
    string Result) : InventoryEvent(Timestamp);

/// <summary>
/// <c>&lt;UnstowPendingEntities&gt; Unstow Request[N] ... finalized spawn of
/// '&lt;class&gt;_&lt;geid&gt;' [&lt;geid&gt;]</c>.
/// <para>
/// Shares its request number with the class-level move that triggered it, which is the only
/// way to learn <i>which</i> instance an equip actually took. Measured over the 4.9 corpus
/// this resolves 532 of 540 Type[Interaction] moves.
/// </para>
/// </summary>
public sealed record EntitySpawned(
    DateTimeOffset Timestamp,
    int RequestNo,
    string Geid,
    string ClassName) : InventoryEvent(Timestamp);

/// <summary>
/// <c>&lt;OnInventoryStoreItem&gt; Entity[&lt;class&gt;_&lt;geid&gt; - Class(...)] Inventory[ref]</c> —
/// an instance-level arrival, independent of the request/completion handshake.
/// </summary>
public sealed record ItemStored(
    DateTimeOffset Timestamp,
    string Geid,
    string ClassName,
    InventoryRef Target) : InventoryEvent(Timestamp);

/// <summary>
/// <c>&lt;AttachmentReceived&gt; Player[..] Attachment[&lt;class&gt;_&lt;geid&gt;, &lt;class&gt;, &lt;geid&gt;] Status[..] Port[..]</c>.
/// <para>
/// The closest the log comes to a snapshot: an authoritative enumeration of the ports worn
/// on the player's body, re-emitted on every spawn. It proves an item is <i>not</i> at a
/// station, which no move line can.
/// </para>
/// </summary>
/// <param name="Status">
/// <c>persistent</c> for an entity the server holds. <c>local</c> is the client's own prediction
/// while an equip is in flight: short made-up geids (10721) and a class of <c>Default</c>, followed
/// within seconds by the persistent line for the real entity.
/// </param>
public sealed record AttachmentSeen(
    DateTimeOffset Timestamp,
    string Geid,
    string ClassName,
    string Port,
    string Status = AttachmentSeen.Persistent) : InventoryEvent(Timestamp)
{
    public const string Persistent = "persistent";

    /// <summary>
    /// Ports that hold a part of another item rather than something worn on the body. In every
    /// burst of the 2026-09-25 playtest each comes right after the item it belongs to:
    /// <c>Armor_Helmet</c> then <c>universal_necksock</c> and <c>helmet_visor</c>, and
    /// <c>wep_stocked_N</c> then <c>magazine_attach</c>, <c>optics_attach</c>,
    /// <c>barrel_attach</c> and <c>underbarrel_attach</c>. In the August logs a multitool
    /// comes the same way, with its <c>magazine_attach</c>, <c>module_attach</c> and
    /// <c>canister_attach</c>. <c>magazine_attach_N</c> is not a part: it is a magazine slot on
    /// the armour as often as a multitool's canister.
    /// </summary>
    private static readonly HashSet<string> PartPorts = new(StringComparer.Ordinal)
    {
        "helmet_visor", "universal_necksock",
        "magazine_attach", "optics_attach", "barrel_attach", "underbarrel_attach",
        "module_attach", "canister_attach",
    };

    /// <summary>Whether a port holds a part of another item. Its name repeats on every weapon.</summary>
    public static bool IsPartPort(string port) => PartPorts.Contains(port);

    public bool IsPart => IsPartPort(Port);

    public bool IsPersistent => Status == Persistent;

    /// <summary>
    /// Whether this is one of the stand-in items the game attaches on every login before the
    /// real loadout arrives.
    /// <para>
    /// About 20 s after launch the game attaches a fixed set of 23 items — an odyssey undersuit
    /// and helmet, a klwe energy pistol with two magazines, a medpen, default hair and face —
    /// all under geids of the form <c>2000xxxxxxxx</c>. The real character follows 40–160 s
    /// later under real geids. Across every log from build 12344265 (2026-08-04) to 12660092
    /// these geids only ever come 23 at a time and never appear in a move line; the player
    /// confirmed owning none of it.
    /// </para>
    /// </summary>
    public bool IsPlaceholder =>
        Geid.Length == 12 && Geid.StartsWith("2000", StringComparison.Ordinal) && Geid.All(char.IsAsciiDigit);
}

/// <summary>
/// <c>&lt;Calculate Route&gt; ... Projected Start Location is &lt;name&gt; for route to destination ...</c> —
/// the player-facing name of wherever the player is standing, and the only place the game
/// ever writes one. The internal <see cref="LocationNamed"/> string is a legacy asset name
/// ("RR_JP_NyxCastra") and does not match it ("Stanton Gateway").
/// </summary>
public sealed record PlaceNamed(
    DateTimeOffset Timestamp,
    string DisplayName) : InventoryEvent(Timestamp);

/// <summary>
/// A streamed object-container path, e.g.
/// <c>Data/objectcontainers/pu/loc/mod/nyx/station/ser/reststop_ext/rs_ext_nyx-castra_jp1.socpak</c>.
/// The segment after <c>loc/mod/</c> or <c>loc/flagship/</c> is the host solar system.
/// </summary>
public sealed record PlaceAssetSeen(
    DateTimeOffset Timestamp,
    string System) : InventoryEvent(Timestamp);

/// <summary>
/// <c>&lt;Inventory Token Flow&gt; ... on Inventory[&lt;class&gt;_&lt;geid&gt;, &lt;geid&gt;]</c> —
/// the only line that names a container entity.
/// </summary>
public sealed record ContainerIdentified(
    DateTimeOffset Timestamp,
    string Geid,
    string ClassName) : InventoryEvent(Timestamp);

/// <summary>
/// <c>&lt;OnDragInventoryItemModifyTarget&gt; Source[&lt;inv&gt;] Capacity[n] ... | Target[&lt;inv&gt;] Capacity[n] ...</c> —
/// how much each end of a drag can hold, in µSCU. For a box the game never names this is the
/// only description of it there is: 2,000,000 µSCU is the 2 SCU crate the player is looking at.
/// A capacity of -1 means the side is INVALID and says nothing.
/// </summary>
public sealed record ContainerCapacitySeen(
    DateTimeOffset Timestamp,
    InventoryRef Source,
    long SourceCapacity,
    InventoryRef Target,
    long TargetCapacity) : InventoryEvent(Timestamp);

/// <summary>
/// Where the player currently is, tracked from <see cref="LocationChanged"/>. Not an
/// event in its own right — the ingestor carries it so that opening a container can be
/// pinned to a place.
/// </summary>
public sealed record PlayerLocation(string LocationId, DateTimeOffset Since);

/// <summary>
/// <c>&lt;Update Inventory Location&gt;</c>. The ids here are numeric only; the human
/// name arrives on the next <see cref="LocationNamed"/>.
/// </summary>
/// <summary>
/// The player picked a quantum travel destination: a point the game names by its asset code
/// (<c>ab_mine_stanton3_sml_003</c>, <c>rs_ext_nyx-castra_jp1</c>).
/// </summary>
public sealed record QuantumTargetSelected(
    DateTimeOffset Timestamp,
    string Point) : InventoryEvent(Timestamp);

/// <summary>The quantum drive reached the final destination of the selected route.</summary>
public sealed record QuantumArrived(DateTimeOffset Timestamp) : InventoryEvent(Timestamp);

public sealed record LocationChanged(
    DateTimeOffset Timestamp,
    string NewLandingId,
    string NewLocationId) : InventoryEvent(Timestamp);

/// <summary>
/// <c>&lt;RequestLocationInventory&gt; ... Location[Nyx_Kaboos]</c> — the name half of
/// the id/name pairing.
/// </summary>
public sealed record LocationNamed(
    DateTimeOffset Timestamp,
    string Name) : InventoryEvent(Timestamp);
