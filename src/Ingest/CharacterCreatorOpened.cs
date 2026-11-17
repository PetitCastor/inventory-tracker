namespace InventoryTracker.Ingest;

/// <summary>
/// The client opened its channel to the character customizer service: the player is in the
/// character creator.
/// <para>
/// A character reset — the kind a patch forces, where the player recreates their character and
/// picks a new home location — is the only thing seen to open it before the player first spawns.
/// Across 119 local sessions from 2026-08 to 2026-11 it happened exactly three times, each in the
/// first session of a new build (12519617, 12881860, 13165946), and each time the player then
/// spawned at the home location they had chosen. No ordinary login opened it.
/// </para>
/// </summary>
public sealed record CharacterCreatorOpened(DateTimeOffset Timestamp) : InventoryEvent(Timestamp);
