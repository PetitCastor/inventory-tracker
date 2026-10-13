namespace InventoryTracker.Ingest;

/// <summary>
/// <c>&lt;CEntityComponentShipListProvider::SetVehicleSpawningInformations&gt; SetVehicleSpawningInformations -
/// VehicleEntityId: [876737712932], LandingArea: ...</c> — a ship was called out of storage.
/// <para>
/// The ship comes out where the player stands, so the player's location dates and places it.
/// The landing area's name runs onto the next log line ("Pilot's" then "Large Hangar") and is
/// not needed for that. Storing a ship writes no line of its own.
/// </para>
/// </summary>
public sealed record VehicleRetrieved(DateTimeOffset Timestamp, string Geid) : InventoryEvent(Timestamp);
