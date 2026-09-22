using InventoryTracker.Model;

namespace InventoryTracker.Resolve;

/// <summary>
/// Replays every recorded move in order to work out what is sitting where.
/// <para>
/// The logs never enumerate an inventory's contents — <c>QueryInventory</c> answers
/// <c>Item[0]</c> every single time — so there is no snapshot to anchor to. The only thing
/// available is the stream of deltas, and the only honest way to read it is to actually
/// fold it: something arrives somewhere, and stays there until something else says it left.
/// </para>
/// <para>
/// Roughly half the deltas name the exact entity that moved; the rest name only a class and
/// a quantity, because the game does not say which of several identical magazines was
/// dragged. Both are folded into the same ledger. A class-level move relocates a named
/// instance when one is sitting at its source — that is what makes an item disappear from
/// the station it was stored at once it is picked back up — and falls back to an anonymous
/// count when it cannot. Counts are what let stacked ammunition, drinks and loose crates
/// appear at all, none of which is ever named.
/// </para>
/// </summary>
public sealed class LedgerReplay
{
    /// <summary>One entity, tracked by geid.</summary>
    public sealed record Instance(
        string Geid,
        string ItemClass,
        DateTimeOffset Since,
        string ArrivedBy,
        bool Confirmed,
        bool IdentityAmbiguous,
        bool Inferred);

    /// <summary>An unnamed quantity of one class, with the newest arrival that contributed to it.</summary>
    public sealed record Anonymous(
        string ItemClass,
        int Quantity,
        DateTimeOffset Since,
        string ArrivedBy,
        bool Confirmed,
        bool Inferred);

    /// <summary>What one holding contains, by item class.</summary>
    private sealed class Slot
    {
        public List<Instance> Named { get; } = [];
        public Anonymous? Loose { get; set; }
    }

    private readonly Dictionary<Holding, Dictionary<string, Slot>> _contents = [];
    private readonly Dictionary<string, Holding> _instanceAt = [];

    /// <summary>Entities that have already absorbed one inferred anonymous unit, so the
    /// same guess is never collapsed into an entity twice.</summary>
    private readonly HashSet<string> _reconciled = [];

    /// <summary>Where each named entity ended up. Containers are entities too, so this is
    /// also how a backpack's own position is found when building a chain.</summary>
    public IReadOnlyDictionary<string, Holding> InstanceAt => _instanceAt;

    /// <summary>Every holding that ended up with something in it.</summary>
    public IEnumerable<Holding> Occupied => _contents.Keys;

    public IReadOnlyList<Instance> NamedIn(Holding where, string itemClass) =>
        SlotFor(where, itemClass, create: false)?.Named ?? (IReadOnlyList<Instance>)[];

    /// <summary>Classes present in a holding, named and anonymous alike.</summary>
    public IEnumerable<(string ItemClass, IReadOnlyList<Instance> Named, Anonymous? Loose)> Contents(Holding where)
    {
        if (!_contents.TryGetValue(where, out var byClass)) yield break;

        foreach (var (itemClass, slot) in byClass)
        {
            if (slot.Named.Count == 0 && slot.Loose is null) continue;
            yield return (itemClass, slot.Named, slot.Loose);
        }
    }

    /// <summary>
    /// Folds the whole move history. Moves must arrive oldest first; the caller's ordering
    /// is the ledger's ordering, since a delta stream only means anything in sequence.
    /// </summary>
    public static LedgerReplay Run(IEnumerable<MoveRecord> moves, IEnumerable<WornSighting> worn)
    {
        var ledger = new LedgerReplay();

        // Attachment sightings are enumerations of the player's body rather than moves, so
        // they are merged into the same timeline and applied by timestamp like anything else.
        var timeline = moves
            .Select(m => (m.Timestamp, Move: (MoveRecord?)m, Worn: (WornSighting?)null))
            .Concat(worn.Select(w => (w.Timestamp, Move: (MoveRecord?)null, Worn: (WornSighting?)w)))
            .OrderBy(x => x.Timestamp);

        foreach (var entry in timeline)
        {
            if (entry.Move is { } move) ledger.Apply(move);
            else if (entry.Worn is { } sighting) ledger.ApplyWorn(sighting);
        }

        return ledger;
    }

    /// <summary>An entity observed attached to the player's body at a point in time.</summary>
    public readonly record struct WornSighting(DateTimeOffset Timestamp, string Geid, string ItemClass);

    private void Apply(MoveRecord move)
    {
        // The game said this one did not happen, so the world never changed.
        if (move.Failed) return;

        var target = new Holding(move.TargetKind, move.TargetKey);
        if (!target.IsReal) return;

        var source = new Holding(move.SourceKind, move.SourceKey);

        if (move.ItemGeid is { } geid)
        {
            PlaceInstance(geid, move.ItemClass, target, move.Timestamp, move.MoveType, move.Succeeded, inferred: false);
            return;
        }

        MoveAnonymous(move, source, target);
    }

    private void ApplyWorn(WornSighting sighting)
    {
        // An attachment line is proof the item is on the player right now, which no move
        // line can give. It supersedes whatever the ledger believed until something later
        // moves the item off again.
        PlaceInstance(
            sighting.Geid, sighting.ItemClass, new Holding(InventoryKind.Equipped, ""),
            sighting.Timestamp, "Worn", confirmed: true, inferred: false);
    }

    /// <summary>
    /// Moves <see cref="MoveRecord.Units"/> of a class between holdings. Named instances at
    /// the source are preferred over anonymous count, newest first: a class-level move is
    /// almost always the player picking back up what they most recently put down, and
    /// relocating a real instance is what removes it from the place it was stored.
    /// </summary>
    private void MoveAnonymous(MoveRecord move, Holding source, Holding target)
    {
        var wanted = move.Units;
        var taken = 0;

        if (source.IsReal && SlotFor(source, move.ItemClass, create: false) is { } from)
        {
            var candidates = from.Named.Count;

            while (taken < wanted && from.Named.Count > 0)
            {
                var pick = from.Named[^1];
                from.Named.RemoveAt(from.Named.Count - 1);

                // _instanceAt is deliberately left pointing at the source: clearing it here
                // would make the move look like a first sighting and spend an unrelated
                // unnamed unit to pay for it.
                PlaceInstance(
                    pick.Geid, move.ItemClass, target, move.Timestamp, move.MoveType,
                    move.Succeeded, inferred: false, ambiguous: candidates > 1 || pick.IdentityAmbiguous);
                taken++;
            }

            while (taken < wanted && from.Loose is { Quantity: > 0 } loose)
            {
                from.Loose = loose.Quantity > 1 ? loose with { Quantity = loose.Quantity - 1 } : null;
                taken++;
                AddAnonymous(target, move.ItemClass, 1, move.Timestamp, move.MoveType, move.Succeeded, inferred: false);
            }
        }

        // Whatever the source could not account for still arrived at the target: the units
        // existed, we simply never saw them get there. Crediting the destination is what
        // keeps a place's contents honest when its history starts mid-stream.
        if (taken < wanted)
        {
            AddAnonymous(
                target, move.ItemClass, wanted - taken, move.Timestamp, move.MoveType,
                move.Succeeded, inferred: true);
        }
    }

    private void PlaceInstance(
        string geid, string itemClass, Holding target, DateTimeOffset at, string moveType,
        bool confirmed, bool inferred, bool ambiguous = false)
    {
        if (_instanceAt.TryGetValue(geid, out var was) && SlotFor(was, itemClass, create: false) is { } old)
        {
            old.Named.RemoveAll(i => i.Geid == geid);
        }
        else
        {
            // First sighting of this entity. If an unnamed unit of the same class is already
            // sitting where this one is arriving, the two are almost certainly the same
            // thing being described twice — an item dragged in by class and stored by name.
            if (SpendAnonymous(target, itemClass)) _reconciled.Add(geid);
        }

        // A named entity coming to rest where we had earlier only *guessed* an anonymous
        // same-class arrival (an inferred loose unit, credited because a class-level move's
        // source could not account for it) is almost certainly that guess made concrete —
        // the game named the item by class going in and by entity settling down. Collapse
        // the two so the guess does not linger as a phantom once the named item moves on.
        // Bounded to one inferred unit per entity, and to inferred units only, so genuinely
        // counted stacks (ammo, drinks) are never touched. This catches the case the target
        // dedupe above misses: the entity was first seen somewhere else and only later
        // passed through the holding holding the guess.
        if (!_reconciled.Contains(geid) && SpendAnonymous(target, itemClass, inferredOnly: true))
        {
            _reconciled.Add(geid);
        }

        SlotFor(target, itemClass, create: true)!.Named.Add(
            new Instance(geid, itemClass, at, moveType, confirmed, ambiguous, inferred));
        _instanceAt[geid] = target;
    }

    /// <summary>
    /// Consumes one anonymous unit of a class from a holding, used when an entity turns out
    /// to be something already counted there without a name. Scoped to the one holding on
    /// purpose: spending a unit from somewhere else would quietly empty an unrelated place.
    /// </summary>
    private bool SpendAnonymous(Holding where, string itemClass, bool inferredOnly = false)
    {
        if (SlotFor(where, itemClass, create: false) is not { } slot) return false;
        if (slot.Loose is not { Quantity: > 0 } loose) return false;
        if (inferredOnly && !loose.Inferred) return false;

        slot.Loose = loose.Quantity > 1 ? loose with { Quantity = loose.Quantity - 1 } : null;
        return true;
    }

    private void AddAnonymous(
        Holding target, string itemClass, int quantity, DateTimeOffset at, string moveType,
        bool confirmed, bool inferred)
    {
        if (quantity <= 0) return;

        var slot = SlotFor(target, itemClass, create: true)!;
        slot.Loose = slot.Loose is { } existing
            ? existing with
            {
                Quantity = existing.Quantity + quantity,
                Since = at,
                ArrivedBy = moveType,
                Confirmed = existing.Confirmed && confirmed,
                Inferred = existing.Inferred || inferred,
            }
            : new Anonymous(itemClass, quantity, at, moveType, confirmed, inferred);
    }

    private Slot? SlotFor(Holding where, string itemClass, bool create)
    {
        if (!_contents.TryGetValue(where, out var byClass))
        {
            if (!create) return null;
            byClass = [];
            _contents[where] = byClass;
        }

        if (!byClass.TryGetValue(itemClass, out var slot))
        {
            if (!create) return null;
            slot = new Slot();
            byClass[itemClass] = slot;
        }

        return slot;
    }
}
