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
    /// <param name="Inferred">
    /// Some of these units were credited without being found at their source.
    /// </param>
    /// <param name="DuplicateRisk">
    /// Some of those inferred units arrived while the ledger still held the same class
    /// somewhere else — so they may be that item, counted twice. Without this, an inferred
    /// unit is simply the first time the item was seen: looted, bought, or there before the
    /// logs begin.
    /// </param>
    public sealed record Anonymous(
        string ItemClass,
        int Quantity,
        DateTimeOffset Since,
        string ArrivedBy,
        bool Confirmed,
        bool Inferred,
        bool DuplicateRisk = false);

    /// <summary>What one holding contains, by item class.</summary>
    private sealed class Slot
    {
        public List<Instance> Named { get; } = [];
        public Anonymous? Loose { get; set; }
    }

    /// <summary>
    /// What happened to every delta during the fold. None of this changes the result; it is
    /// what makes the replay's reliability measurable rather than a matter of opinion — see
    /// docs/reliability.
    /// </summary>
    public sealed class ReplayStats
    {
        /// <summary>Moves the game said failed, never applied.</summary>
        public int FailedSkipped { get; internal set; }

        /// <summary>Moves whose target is not somewhere an item can be held.</summary>
        public int UnrealTargetSkipped { get; internal set; }

        /// <summary>Moves that named the exact entity.</summary>
        public int NamedMoves { get; internal set; }

        /// <summary>Moves that only named a class.</summary>
        public int ClassMoves { get; internal set; }

        /// <summary>Units the class-level moves asked for.</summary>
        public int ClassUnits { get; internal set; }

        /// <summary>Units found at the source as a named instance.</summary>
        public int UnitsFromNamed { get; internal set; }

        /// <summary>Units found at the source as an anonymous count.</summary>
        public int UnitsFromLoose { get; internal set; }

        /// <summary>
        /// Units the source could not account for, found instead on the player — in a
        /// backpack, worn apparel or equipped. What the player carries travels with them
        /// without a move line, so that is where a missing unit most likely came from.
        /// </summary>
        public int UnitsFromCarried { get; internal set; }

        /// <summary>
        /// Units nobody could account for, credited to the target as a guess:
        /// <see cref="UnitsFirstSeen"/> plus <see cref="UnitsDuplicateRisk"/>.
        /// </summary>
        public int UnitsInferred { get; internal set; }

        /// <summary>
        /// Inferred units of a class the ledger held nowhere else: the item's first
        /// appearance — looted, bought, or already there before the logs begin. Not an error.
        /// </summary>
        public int UnitsFirstSeen { get; internal set; }

        /// <summary>
        /// Inferred units of a class the ledger still held somewhere else. Either that copy
        /// is stale or this one is a double count — the ledger lost track either way.
        /// </summary>
        public int UnitsDuplicateRisk { get; internal set; }

        /// <summary>Class-level moves whose source was a real holding the ledger knew nothing about.</summary>
        public int SourceUnknown { get; internal set; }

        /// <summary>Anonymous units absorbed by a named entity turning up where they were.</summary>
        public int AnonymousReconciled { get; internal set; }

        /// <summary>Attachment sightings applied.</summary>
        public int WornSightings { get; internal set; }

        /// <summary>Items carried in hand that a later body enumeration left out — used up.</summary>
        public int CarriedUsedUp { get; internal set; }

        /// <summary>Every class-level move whose source fell short, and why.</summary>
        public List<SourceMiss> Misses { get; } = [];

        /// <summary>
        /// Share of class-level units found where the move said they came from. The single
        /// best measure of how complete the ledger's history is: every miss is a unit that
        /// arrived somewhere the replay never saw it leave.
        /// </summary>
        public double SourceHitRate =>
            ClassUnits == 0 ? 1.0 : (double)(UnitsFromNamed + UnitsFromLoose) / ClassUnits;

        /// <summary>
        /// Share of class-level units the ledger can account for: found at the source, found
        /// on the player, or the class's first appearance. The rest are duplicate risks.
        /// </summary>
        public double AccountedRate =>
            ClassUnits == 0 ? 1.0 : 1.0 - (double)UnitsDuplicateRisk / ClassUnits;
    }

    public ReplayStats Stats { get; } = new();

    /// <summary>Why a class-level move's source could not supply what it asked for.</summary>
    public enum MissCause
    {
        /// <summary>The move named no source at all — a Store from the hands, say.</summary>
        NoSource,

        /// <summary>Nothing had ever been placed at the source: its history predates what we saw.</summary>
        SourceNeverSeen,

        /// <summary>The source had held things, but never anything of this class.</summary>
        ClassNeverAtSource,

        /// <summary>This class had been at the source, and had already been taken out again.</summary>
        AlreadyTaken,
    }

    /// <summary>
    /// A class-level move that found fewer units at its source than it moved.
    /// </summary>
    /// <param name="ElsewhereAt">
    /// Another holding the ledger had this class at when the move happened, if any — the
    /// sign that the item was really somewhere the ledger lost track of, rather than never
    /// seen at all.
    /// </param>
    /// <param name="CoveredByCarried">The shortfall was found on the player instead.</param>
    public sealed record SourceMiss(
        DateTimeOffset At, string ItemClass, Holding Source, Holding Target,
        int Wanted, int Found, MissCause Cause, Holding? ElsewhereAt, bool CoveredByCarried = false);

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
    /// <param name="carriedContainers">
    /// Containers that travel with the player: backpacks and worn apparel. A class-level move
    /// whose source falls short draws the rest from these before guessing.
    /// </param>
    public static LedgerReplay Run(
        IEnumerable<MoveRecord> moves, IEnumerable<WornSighting> worn, BodyEnumeration? enumeration = null,
        IReadOnlySet<string>? carriedContainers = null)
    {
        var ledger = new LedgerReplay { _carriedContainers = carriedContainers ?? new HashSet<string>() };

        // Attachment sightings are enumerations of the player's body rather than moves, so
        // they are merged into the same timeline and applied by timestamp like anything else.
        var timeline = moves
            .Select(m => (At: m.Timestamp, Move: (MoveRecord?)m, Worn: (WornSighting?)null, Body: (BodyEnumeration?)null))
            .Concat(worn.Select(w => (At: w.Timestamp, Move: (MoveRecord?)null, Worn: (WornSighting?)w, Body: (BodyEnumeration?)null)));

        if (enumeration is not null)
        {
            timeline = timeline.Append((At: enumeration.At, Move: null, Worn: null, Body: enumeration));
        }

        foreach (var entry in timeline.OrderBy(x => x.At))
        {
            if (entry.Move is { } move) ledger.Apply(move);
            else if (entry.Worn is { } sighting) ledger.ApplyWorn(sighting);
            else if (entry.Body is { } body) ledger.UseUpCarried(body);
        }

        return ledger;
    }

    /// <summary>An entity observed attached to the player's body at a point in time.</summary>
    public readonly record struct WornSighting(DateTimeOffset Timestamp, string Geid, string ItemClass);

    /// <summary>
    /// The newest complete listing of what the player has on them — the burst of attachment
    /// lines the game writes whenever the character streams in — as the entities and classes
    /// it named.
    /// </summary>
    public sealed record BodyEnumeration(
        DateTimeOffset At, IReadOnlySet<string> Geids, IReadOnlySet<string> Classes);

    private const string CarryArrival = "Carry";

    private IReadOnlySet<string> _carriedContainers = new HashSet<string>();

    /// <summary>
    /// Entities last put on the player by being carried in hand, and not moved anywhere since.
    /// A worn sighting does not clear this: the hand and pocket ports are exactly where a
    /// carried item is sighted.
    /// </summary>
    private readonly HashSet<string> _carried = [];

    /// <summary>
    /// Removes what was carried in hand before <paramref name="body"/> but is missing from it.
    /// <para>
    /// Food, drink and mission items picked up to be used vanish without any line saying so.
    /// Every one of the 5 items carried and never stored back in the local corpus is absent
    /// from every enumeration afterwards, while an enumeration re-lists anything still held.
    /// So absence is the evidence that it was used up. An item carried after the enumeration
    /// has not been contradicted yet and stays.
    /// </para>
    /// </summary>
    private void UseUpCarried(BodyEnumeration body)
    {
        if (!_contents.TryGetValue(new Holding(InventoryKind.Equipped, ""), out var byClass)) return;

        foreach (var (itemClass, slot) in byClass)
        {
            foreach (var instance in slot.Named.ToList())
            {
                if (!_carried.Contains(instance.Geid)) continue;
                if (instance.Since >= body.At || body.Geids.Contains(instance.Geid)) continue;

                slot.Named.Remove(instance);
                _carried.Remove(instance.Geid);
                _instanceAt.Remove(instance.Geid);
                Stats.CarriedUsedUp++;
            }

            // A carry the log never named: its class is all there is to look for.
            if (slot.Loose is { ArrivedBy: CarryArrival } loose
                && loose.Since < body.At && !body.Classes.Contains(itemClass))
            {
                Stats.CarriedUsedUp += loose.Quantity;
                slot.Loose = null;
            }
        }
    }

    private void Apply(MoveRecord move)
    {
        // The game said this one did not happen, so the world never changed.
        if (move.Failed)
        {
            Stats.FailedSkipped++;
            return;
        }

        var target = new Holding(move.TargetKind, move.TargetKey);
        if (!target.IsReal)
        {
            Stats.UnrealTargetSkipped++;
            return;
        }

        var source = new Holding(move.SourceKind, move.SourceKey);

        if (move.ItemGeid is { } geid)
        {
            Stats.NamedMoves++;
            if (move.ArrivedBy == CarryArrival) _carried.Add(geid);
            else _carried.Remove(geid);

            PlaceInstance(
                geid, move.ItemClass, target, move.Timestamp, move.ArrivedBy, move.Succeeded, inferred: false,
                source: source);
            return;
        }

        MoveAnonymous(move, source, target);
    }

    private void ApplyWorn(WornSighting sighting)
    {
        Stats.WornSightings++;

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
        // A Store never names its source because it is always the player: the item leaves the
        // hand or a body port for a grid. Left INVALID, a class-level Store could never find
        // what it takes, and the canister put away would stay equipped as well.
        if (!source.IsReal && move.MoveType == "Store") source = new Holding(InventoryKind.Equipped, "");

        var wanted = move.Units;
        var taken = 0;

        Stats.ClassMoves++;
        Stats.ClassUnits += wanted;

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
                    pick.Geid, move.ItemClass, target, move.Timestamp, move.ArrivedBy,
                    move.Succeeded, inferred: false, ambiguous: candidates > 1 || pick.IdentityAmbiguous);
                taken++;
                Stats.UnitsFromNamed++;
            }

            while (taken < wanted && from.Loose is { Quantity: > 0 } loose)
            {
                from.Loose = loose.Quantity > 1 ? loose with { Quantity = loose.Quantity - 1 } : null;
                taken++;
                Stats.UnitsFromLoose++;
                AddAnonymous(target, move.ItemClass, 1, move.Timestamp, move.ArrivedBy, move.Succeeded, inferred: false);
            }
        }
        else if (source.IsReal)
        {
            Stats.SourceUnknown++;
        }

        // Only a move that names a real source can be short of it. One with no source at all
        // says nothing about where the item was, and raiding the backpack for it would empty
        // the backpack on the strength of nothing.
        var fromSource = taken;
        if (taken < wanted && source.IsReal) taken += TakeFromCarried(move, source, target, wanted - taken);

        // Whatever nothing could account for still arrived at the target: the units existed,
        // we simply never saw them get there. Crediting the destination is what keeps a
        // place's contents honest when its history starts mid-stream.
        if (taken < wanted)
        {
            var miss = DescribeMiss(move, source, target, wanted, taken);
            Stats.Misses.Add(miss);

            // Only a class the ledger still holds elsewhere can be counted twice. Anything
            // else is the item's first appearance, which is information, not an error.
            var duplicateRisk = miss.ElsewhereAt is not null;
            var units = wanted - taken;
            Stats.UnitsInferred += units;
            if (duplicateRisk) Stats.UnitsDuplicateRisk += units;
            else Stats.UnitsFirstSeen += units;

            AddAnonymous(
                target, move.ItemClass, units, move.Timestamp, move.ArrivedBy,
                move.Succeeded, inferred: true, duplicateRisk);
        }
        else if (taken > fromSource && source.IsReal)
        {
            Stats.Misses.Add(DescribeMiss(move, source, target, wanted, fromSource) with { CoveredByCarried = true });
        }
    }

    /// <summary>
    /// Draws units a class-level move's source could not supply from what the player carries:
    /// equipped, or inside a backpack or worn apparel. Magazines are the typical case — they
    /// ride along in a backpack or on armour and turn up in a station stack with no move line
    /// in between. Named instances first, like at the source.
    /// </summary>
    private int TakeFromCarried(MoveRecord move, Holding source, Holding target, int wanted)
    {
        var taken = 0;
        foreach (var carrier in CarriedHoldings())
        {
            if (taken == wanted) break;
            if (carrier == source || carrier == target) continue;
            if (SlotFor(carrier, move.ItemClass, create: false) is not { } slot) continue;

            while (taken < wanted && slot.Named.Count > 0)
            {
                var pick = slot.Named[^1];
                slot.Named.RemoveAt(slot.Named.Count - 1);
                PlaceInstance(
                    pick.Geid, move.ItemClass, target, move.Timestamp, move.ArrivedBy,
                    move.Succeeded, inferred: false, ambiguous: true);
                taken++;
            }

            while (taken < wanted && slot.Loose is { Quantity: > 0 } loose)
            {
                slot.Loose = loose.Quantity > 1 ? loose with { Quantity = loose.Quantity - 1 } : null;
                AddAnonymous(target, move.ItemClass, 1, move.Timestamp, move.ArrivedBy, move.Succeeded, inferred: false);
                taken++;
            }
        }

        Stats.UnitsFromCarried += taken;
        return taken;
    }

    private IEnumerable<Holding> CarriedHoldings()
    {
        yield return new Holding(InventoryKind.Equipped, "");
        foreach (var key in _carriedContainers)
        {
            var holding = new Holding(InventoryKind.Container, key);
            if (_contents.ContainsKey(holding)) yield return holding;
        }
    }

    private SourceMiss DescribeMiss(MoveRecord move, Holding source, Holding target, int wanted, int found)
    {
        var cause = !source.IsReal ? MissCause.NoSource
            : !_contents.TryGetValue(source, out var byClass) ? MissCause.SourceNeverSeen
            : !byClass.ContainsKey(move.ItemClass) ? MissCause.ClassNeverAtSource
            : MissCause.AlreadyTaken;

        Holding? elsewhere = null;
        foreach (var (where, classes) in _contents)
        {
            if (where == source || where == target) continue;
            if (classes.TryGetValue(move.ItemClass, out var slot) && (slot.Named.Count > 0 || slot.Loose is not null))
            {
                elsewhere = where;
                break;
            }
        }

        return new SourceMiss(move.Timestamp, move.ItemClass, source, target, wanted, found, cause, elsewhere);
    }

    private void PlaceInstance(
        string geid, string itemClass, Holding target, DateTimeOffset at, string moveType,
        bool confirmed, bool inferred, bool ambiguous = false, Holding? source = null)
    {
        if (_instanceAt.TryGetValue(geid, out var was) && SlotFor(was, itemClass, create: false) is { } old)
        {
            old.Named.RemoveAll(i => i.Geid == geid);
        }
        else if (source is { IsReal: true } from && SpendAnonymous(from, itemClass))
        {
            // First sighting, on its way out of somewhere that held an unnamed unit of its
            // class: that unit is this entity, now named. Leaving it behind would count the
            // item twice — a drink carried out of a crate would still be in the crate too.
            _reconciled.Add(geid);
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
        Stats.AnonymousReconciled++;
        return true;
    }

    private void AddAnonymous(
        Holding target, string itemClass, int quantity, DateTimeOffset at, string moveType,
        bool confirmed, bool inferred, bool duplicateRisk = false)
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
                DuplicateRisk = existing.DuplicateRisk || duplicateRisk,
            }
            : new Anonymous(itemClass, quantity, at, moveType, confirmed, inferred, duplicateRisk);
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
