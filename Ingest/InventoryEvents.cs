using LogParser.Model;

namespace LogParser.Ingest;

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
    int Amount) : InventoryEvent(Timestamp);

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

/// <summary>Outcome for a request, from either of the two "complete" events.</summary>
public sealed record MoveCompleted(
    DateTimeOffset Timestamp,
    int RequestNo,
    string Result) : InventoryEvent(Timestamp);

/// <summary>
/// <c>&lt;Inventory Token Flow&gt; ... on Inventory[&lt;class&gt;_&lt;geid&gt;, &lt;geid&gt;]</c> —
/// the only line that names a container entity.
/// </summary>
public sealed record ContainerIdentified(
    DateTimeOffset Timestamp,
    string Geid,
    string ClassName) : InventoryEvent(Timestamp);

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
