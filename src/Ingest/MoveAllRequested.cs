using InventoryTracker.Model;

namespace InventoryTracker.Ingest;

/// <summary>
/// The player moved everything in one inventory to another at once, with the inventory
/// screen's move-all button:
/// <c>&lt;Add Inventory Management Move&gt; New Request[N] ... Type[Move] SourceInventory[..]
/// TargetInventory[..] ItemClass[] ...</c>.
/// <para>
/// The line names no item and no quantity, and its Queued counterpart degenerates to
/// <c>Source[NULL] amount[0] ... Item[NONE]</c>. So the only way to know what went is what
/// the tracker already believed was in the source. Seen three times, all on 2026-11-22 at
/// QV Logistics Station: a backpack emptied into the station at 14:15:36, then the station
/// emptied into the backpack at 18:32:48 and back again at 18:32:50. The Fizzz cans that
/// went in at 14:15 were later taken out of the station by name, at 14:16 and at 18:37,
/// which is what proved they had gone in.
/// </para>
/// <para>
/// Best effort: if the target had no room for everything, the game left the rest behind,
/// and the log does not say which items.
/// </para>
/// </summary>
public sealed record MoveAllRequested(
    DateTimeOffset Timestamp,
    int RequestNo,
    InventoryRef Source,
    InventoryRef Target,
    string Caller) : InventoryEvent(Timestamp);
