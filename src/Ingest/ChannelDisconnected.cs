namespace InventoryTracker.Ingest;

/// <summary>
/// <c>&lt;Channel Disconnected&gt; cause=30016 reason="Remote Disconnect - Player requested disconnect" ...</c> —
/// the client's connection to a server closed.
/// <para>
/// Whatever inventory request the server had not processed by then is gone. On 2026-09-25 the
/// server's queue stalled at 15:44; four moves the player made afterwards were queued, never
/// processed, and dropped when the player switched servers at 15:53:23. After the reconnect the
/// player found all three helmets back where they had been taken from.
/// </para>
/// <para>
/// The game writes one on every login too (cause 30010, "Nub destroyed", as the front end hands
/// over to the game server), when nothing of the session is pending yet.
/// </para>
/// </summary>
public sealed record ChannelDisconnected(DateTimeOffset Timestamp, int Cause, string Reason) : InventoryEvent(Timestamp);
