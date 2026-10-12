namespace InventoryTracker.Ingest;

/// <summary>
/// <c>&lt;Vehicle Control Flow&gt; CVehicleMovementBase::ClearDriver: Local client node [..] releasing
/// control token for 'GLSN_Shiv_876737712932' [876737712932]</c> — the player left a pilot seat.
/// <para>
/// The only line that names a ship's class together with its geid. Taking the seat writes no
/// line at this log level, so a ship is known once the player has flown it and got up again.
/// </para>
/// </summary>
public sealed record VehicleFlown(DateTimeOffset Timestamp, string Geid, string ClassName) : InventoryEvent(Timestamp);
