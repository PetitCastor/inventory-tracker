namespace InventoryTracker.Ingest;

/// <summary>
/// One item left on the player's body when they died:
/// <c>&lt;Adding non kept item [CSCActorCorpseUtils::PopulateItemPortForItemRecoveryEntitlement]&gt;
/// Item '&lt;class&gt;_&lt;geid&gt; - Class(&lt;class&gt;) - ...', Recorded data is: Port Name '&lt;port&gt;', ...</c>
/// <para>
/// The game writes one per port in a single burst, all on the same millisecond: the body, the
/// armour, the backpack, every weapon with its parts, the magazines on the armour, the
/// multitool and the character's own head and mobiGlas. It lists what hangs on the body and
/// nothing inside a container: on 2026-11-21 the backpack's line came alone, with the six
/// items stored in it never named. Seconds later the game attaches a fresh loadout of the same
/// classes under new geids.
/// </para>
/// <para>
/// A death that leaves no corpse writes none of these: at the medbed respawn of 2026-09-25
/// 20:08 the game re-sent the same geids and the backpack kept what was in it.
/// </para>
/// </summary>
public sealed record CorpseItemListed(
    DateTimeOffset Timestamp,
    string Geid,
    string ClassName,
    string Port) : InventoryEvent(Timestamp);
