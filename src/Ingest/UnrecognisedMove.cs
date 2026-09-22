namespace InventoryTracker.Ingest;

/// <summary>
/// The canary for log-grammar drift. Emitted instead of silently returning null when a line
/// carries every visible sign of being an inventory move request — a "Queued Request[" or
/// "New request[" / "New Request[" body, on either a dispatched event tag whose regex no
/// longer matches it or an event tag we have never dispatched on at all — but none of
/// <see cref="InventoryEventParser"/>'s patterns can actually read it.
/// <para>
/// The game has already done this once: build 12519617 renamed
/// <c>&lt;InventoryManagementRequest&gt;</c> to <c>&lt;Inventory Mgmt Request Queued&gt;</c> and
/// capitalised <c>New request[</c> to <c>New Request[</c>, and both changes shipped with the
/// line bodies otherwise byte-identical. Nothing else in the pipeline would have noticed —
/// the ingestor just quietly stopped recording moves. Counting this event turns the next such
/// change into a number going up in the stats block instead of "my stuff vanished".
/// </para>
/// </summary>
public sealed record UnrecognisedMove(DateTimeOffset Timestamp, string EventName) : InventoryEvent(Timestamp);
