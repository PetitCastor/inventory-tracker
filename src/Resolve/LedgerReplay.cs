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
    /// <param name="MovedByUpdate">
    /// The game build whose update the ledger assumed moved this entity here, rather than a
    /// move line. <see cref="Since"/> stays the last time the log saw it.
    /// </param>
    public sealed record Instance(
        string Geid,
        string ItemClass,
        DateTimeOffset Since,
        string ArrivedBy,
        bool Confirmed,
        bool IdentityAmbiguous,
        bool Inferred,
        int? MovedByUpdate = null);

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
    /// <param name="MovedByUpdate">
    /// Some of these units were assumed moved here by this build's update.
    /// </param>
    public sealed record Anonymous(
        string ItemClass,
        int Quantity,
        DateTimeOffset Since,
        string ArrivedBy,
        bool Confirmed,
        bool Inferred,
        bool DuplicateRisk = false,
        int? MovedByUpdate = null);

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

        /// <summary>Moves the server never processed before the connection closed, never applied.</summary>
        public int LostSkipped { get; internal set; }

        /// <summary>
        /// Units a lost move proved were at its source, credited there because the ledger had
        /// none.
        /// </summary>
        public int UnitsCreditedByLost { get; internal set; }

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
        /// Units the source could not account for, found instead among entities the ledger had
        /// lost track of.
        /// </summary>
        public int UnitsFromLostTrack { get; internal set; }

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

        /// <summary>
        /// Units on the player that a later body enumeration no longer listed, and whose
        /// whereabouts are therefore unknown.
        /// </summary>
        public int WornEvicted { get; internal set; }

        /// <summary>
        /// Evicted entities sighted on the player again with no move in between. Each one is an
        /// enumeration that left out something still worn: the eviction was wrong. Expected 0.
        /// </summary>
        public int EvictedThenResighted { get; internal set; }

        /// <summary>Units a game update was assumed to have moved to where the player next spawned.</summary>
        public int RelocatedByUpdate { get; internal set; }

        /// <summary>Every class-level move whose source fell short, and why.</summary>
        public List<SourceMiss> Misses { get; } = [];

        /// <summary>
        /// Every named move out of a real source, checked against where the ledger believed
        /// the entity was. This is the only ground truth the logs give on whether a placement
        /// still held — the game names the source, and the ledger had its own answer.
        /// </summary>
        public List<PlacementCheck> PlacementChecks { get; } = [];

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

    /// <summary>
    /// One named move whose source could be compared with the ledger's belief.
    /// </summary>
    /// <param name="Believed">Where the ledger had the entity when the move happened.</param>
    /// <param name="Age">How long it had been there, by the ledger's account.</param>
    /// <param name="Actual">Where the game said the entity was moved from.</param>
    public sealed record PlacementCheck(Holding Believed, TimeSpan Age)
    {
        public string Geid { get; init; } = "";
        public string ItemClass { get; init; } = "";
        public DateTimeOffset At { get; init; }
        public Holding Actual { get; init; }
        public string BelievedArrivedBy { get; init; } = "";

        /// <summary>The belief was a game update's relocation, not a move line.</summary>
        public int? BelievedMovedByUpdate { get; init; }

        /// <summary>
        /// Believed and actual resolve to the same place once containers are followed out to
        /// where they sit — an item in a backpack stored at a station is at that station.
        /// </summary>
        public bool SamePlace { get; init; }
    }

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
    /// <param name="relocations">
    /// Game updates, and where each moved the player's belongings to.
    /// </param>
    /// <param name="enumerations">
    /// Every complete listing of the player's body, each authoritative for what was on the
    /// player at that moment.
    /// </param>
    public static LedgerReplay Run(
        IEnumerable<MoveRecord> moves, IEnumerable<WornSighting> worn,
        IEnumerable<BodyEnumeration>? enumerations = null,
        IReadOnlySet<string>? carriedContainers = null, IEnumerable<Relocation>? relocations = null)
    {
        var ledger = new LedgerReplay { _carriedContainers = carriedContainers ?? new HashSet<string>() };

        // Attachment sightings are enumerations of the player's body rather than moves, so
        // they are merged into the same timeline and applied by timestamp like anything else.
        var timeline = moves
            .Select(m => new Entry(m.Timestamp, Move: m))
            .Concat(worn.Select(w => new Entry(w.Timestamp, Worn: w)))
            .Concat((enumerations ?? []).Select(e => new Entry(e.At, Body: e)))
            .Concat((relocations ?? Enumerable.Empty<Relocation>()).Select(r => new Entry(r.At, Update: r)));

        // An update lands before anything logged in the build it starts. Otherwise ties keep
        // the order above, which the sort preserves: at one instant the moves apply first, then
        // the sightings, then the listing — so a listing sees the items and parts sighted in its
        // own burst already in place.
        foreach (var entry in timeline.OrderBy(x => x.At).ThenBy(x => x.Update is null ? 1 : 0))
        {
            if (entry.Move is { } move) ledger.Apply(move);
            else if (entry.Worn is { } sighting) ledger.ApplyWorn(sighting);
            else if (entry.Body is { } body) ledger.ApplyEnumeration(body);
            else if (entry.Update is { } update) ledger.Relocate(update);
        }

        return ledger;
    }

    /// <summary>Where an entity is once a body enumeration no longer lists it.</summary>
    public static readonly Holding LostTrack = new(InventoryKind.LostTrack, "");

    /// <summary>The entity with this geid, wherever the ledger holds it.</summary>
    public Instance? Find(string geid)
    {
        if (!_instanceAt.TryGetValue(geid, out var at) || !_contents.TryGetValue(at, out var byClass)) return null;

        foreach (var slot in byClass.Values)
        {
            if (slot.Named.Find(i => i.Geid == geid) is { } instance) return instance;
        }

        return null;
    }

    private sealed record Entry(
        DateTimeOffset At, MoveRecord? Move = null, WornSighting? Worn = null, BodyEnumeration? Body = null,
        Relocation? Update = null);

    /// <summary>
    /// A game update, and the station the player first spawned at in the build it started.
    /// </summary>
    public sealed record Relocation(DateTimeOffset At, int Build, Holding Destination);

    /// <summary>
    /// Moves everything stored at a station or on the player to <paramref name="update"/>'s
    /// destination.
    /// <para>
    /// Updates move belongings server-side and log none of it. In the local corpus, every one
    /// of the 13 items checked after build 12519617 was taken out of the station the player
    /// first spawned at: armour stored at Lorville, a rifle stored at Levski, and weapons and
    /// an undersuit last seen equipped. Containers are entities too, so what is inside one
    /// follows it. Dropped items are left where they fell, and so is anything carried in hand:
    /// it is most likely used up, and the next body enumeration settles that.
    /// </para>
    /// </summary>
    private void Relocate(Relocation update)
    {
        var equipped = new Holding(InventoryKind.Equipped, "");
        var from = _contents.Keys
            .Where(h => h != update.Destination && (h.Kind == InventoryKind.Location || h == equipped))
            .ToList();

        foreach (var where in from)
        {
            foreach (var (itemClass, slot) in _contents[where])
            {
                foreach (var instance in slot.Named.Where(i => !_carried.Contains(i.Geid)).ToList())
                {
                    slot.Named.Remove(instance);
                    SlotFor(update.Destination, itemClass, create: true)!.Named.Add(
                        instance with { MovedByUpdate = update.Build });
                    _instanceAt[instance.Geid] = update.Destination;
                    Stats.RelocatedByUpdate++;
                }

                if (slot.Loose is { } loose && loose.ArrivedBy != CarryArrival)
                {
                    slot.Loose = null;
                    MergeLoose(update.Destination, loose with { MovedByUpdate = update.Build });
                    Stats.RelocatedByUpdate += loose.Quantity;
                }
            }
        }
    }

    /// <summary>Adds a whole unnamed stack to a holding, merging with what is already there.</summary>
    private void MergeLoose(Holding target, Anonymous moved)
    {
        var slot = SlotFor(target, moved.ItemClass, create: true)!;
        if (slot.Loose is not { } existing)
        {
            slot.Loose = moved;
            return;
        }

        var newer = existing.Since >= moved.Since ? existing : moved;
        slot.Loose = newer with
        {
            Quantity = existing.Quantity + moved.Quantity,
            Confirmed = existing.Confirmed && moved.Confirmed,
            Inferred = existing.Inferred || moved.Inferred,
            DuplicateRisk = existing.DuplicateRisk || moved.DuplicateRisk,
            MovedByUpdate = moved.MovedByUpdate ?? existing.MovedByUpdate,
        };
    }

    /// <summary>An entity observed attached to the player's body at a point in time.</summary>
    /// <param name="Port">The body port it was attached to, when known.</param>
    /// <param name="IsPart">
    /// A part of another item (a visor, a magazine) rather than something worn on the body.
    /// </param>
    /// <param name="ParentGeid">For a part, the item it hangs off, when the burst says.</param>
    public readonly record struct WornSighting(
        DateTimeOffset Timestamp, string Geid, string ItemClass, string? Port = null, bool IsPart = false,
        string? ParentGeid = null);

    /// <summary>
    /// One complete listing of what the player has on them — the burst of attachment lines
    /// the game writes whenever the character streams in — as the entities and classes it
    /// named.
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
    /// Makes the player's body match <paramref name="body"/>: whatever the ledger had on the
    /// player before it, and it does not list, is no longer there.
    /// <para>
    /// An enumeration re-lists everything worn: across the 2026-09-25 playtest, every relog,
    /// server switch and station respawn re-sent each worn entity under the same geid. So
    /// absence is evidence the item left, and what it means depends on how it got there.
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// Carried in hand: food, drink and mission items picked up to be used vanish without any
    /// line saying so. Every one of the 5 items carried and never stored back in the local
    /// corpus is absent from every enumeration afterwards. It was used up, and is removed.
    /// </item>
    /// <item>
    /// Anything else — worn, equipped: it was stored, sold or lost without a line saying
    /// where. It goes to <see cref="LostTrack"/>, never out of the ledger: an entity with no
    /// placement reads as still on the player, and whatever is inside a backpack follows the
    /// backpack there.
    /// </item>
    /// </list>
    /// <para>
    /// An item placed on the player after the enumeration has not been contradicted yet and
    /// stays until the next one.
    /// </para>
    /// </summary>
    private void ApplyEnumeration(BodyEnumeration body)
    {
        DetachMissingParts(body);

        if (!_contents.TryGetValue(new Holding(InventoryKind.Equipped, ""), out var byClass)) return;

        foreach (var (itemClass, slot) in byClass)
        {
            foreach (var instance in slot.Named.ToList())
            {
                if (instance.Since >= body.At || body.Geids.Contains(instance.Geid)) continue;

                slot.Named.Remove(instance);
                if (_carried.Remove(instance.Geid))
                {
                    _instanceAt.Remove(instance.Geid);
                    Stats.CarriedUsedUp++;
                    continue;
                }

                SlotFor(LostTrack, itemClass, create: true)!.Named.Add(instance);
                _instanceAt[instance.Geid] = LostTrack;
                Stats.WornEvicted++;
            }

            // Unnamed units: their class is all there is to look for.
            if (slot.Loose is not { } loose || loose.Since >= body.At || body.Classes.Contains(itemClass)) continue;

            slot.Loose = null;
            if (loose.ArrivedBy == CarryArrival)
            {
                Stats.CarriedUsedUp += loose.Quantity;
            }
            else
            {
                MergeLoose(LostTrack, loose);
                Stats.WornEvicted += loose.Quantity;
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

        if (move.Lost)
        {
            Stats.LostSkipped++;
            CreditLostSource(move);
            return;
        }

        // The server is processing requests again: whatever the client predicted while it was
        // stalled has been settled one way or the other.
        if (move.Succeeded)
        {
            _predictedOut.Clear();
            _predictedIn.Clear();
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
            NoteCarry(geid, move.ArrivedBy);

            CheckPlacement(geid, move, source);

            PlaceInstance(
                geid, move.ItemClass, target, move.Timestamp, move.ArrivedBy, move.Succeeded, inferred: false,
                source: source);
            return;
        }

        MoveAnonymous(move, source, target);
    }

    /// <summary>
    /// Units the client showed leaving, and arriving at, each place through moves the server
    /// then dropped. The client draws its inventory with its own pending moves applied, so a
    /// later move out of a place counts on what earlier ones seemed to put there.
    /// </summary>
    private readonly Dictionary<(Holding, string), int> _predictedOut = [];
    private readonly Dictionary<(Holding, string), int> _predictedIn = [];

    /// <summary>
    /// A move the server never processed changed nothing, but it does prove the player saw
    /// the item at its source when they dragged it. When the ledger has fewer units there
    /// than that implies, the missing ones are credited to the source.
    /// <para>
    /// On 2026-09-25 the player dragged an Aril helmet and then two Defiance helmets from
    /// Area18 into the backpack, and the Aril back again, all while the server's queue was
    /// stalled. All four moves were dropped and all three helmets were at Area18 after the
    /// reconnect — but the ledger had never seen them there. The Aril's return trip starts in
    /// the backpack only because the client showed it there, so what earlier dropped moves
    /// seemed to deliver is not evidence of anything.
    /// </para>
    /// </summary>
    private void CreditLostSource(MoveRecord move)
    {
        var source = new Holding(move.SourceKind, move.SourceKey);
        if (!source.IsReal) return;

        var key = (source, move.ItemClass);
        var seen = _predictedOut.GetValueOrDefault(key) + move.Units - _predictedIn.GetValueOrDefault(key);
        var held = SlotFor(source, move.ItemClass, create: false) is { } slot
            ? slot.Named.Count + (slot.Loose?.Quantity ?? 0)
            : 0;

        if (seen > held)
        {
            AddAnonymous(source, move.ItemClass, seen - held, move.Timestamp, move.MoveType, confirmed: true, inferred: true);
            Stats.UnitsCreditedByLost += seen - held;
        }

        _predictedOut[key] = _predictedOut.GetValueOrDefault(key) + move.Units;

        var target = new Holding(move.TargetKind, move.TargetKey);
        if (target.IsReal) _predictedIn[(target, move.ItemClass)] = _predictedIn.GetValueOrDefault((target, move.ItemClass)) + move.Units;
    }

    private void ApplyWorn(WornSighting sighting)
    {
        Stats.WornSightings++;
        if (_instanceAt.GetValueOrDefault(sighting.Geid) == LostTrack) Stats.EvictedThenResighted++;

        ReplaceOccupant(sighting);

        // An attachment line is proof the item is on the player right now, which no move
        // line can give. It supersedes whatever the ledger believed until something later
        // moves the item off again.
        //
        // A part — a helmet's visor, a weapon's magazine — goes inside the item it hangs off,
        // so it follows that item wherever its moves take it: the game stores a helmet or a
        // rifle with its parts on and logs a line for the item alone.
        var target = new Holding(InventoryKind.Equipped, "");
        if (sighting is { IsPart: true, ParentGeid: { } parent })
        {
            target = new Holding(InventoryKind.Container, parent);
            _partParents.Add(parent);
        }

        PlaceInstance(sighting.Geid, sighting.ItemClass, target, sighting.Timestamp, "Worn", confirmed: true, inferred: false);
    }

    /// <summary>Entities a part has been sighted on: helmets, weapons, multitools.</summary>
    private readonly HashSet<string> _partParents = [];

    /// <summary>Whether parts hang off this entity, so it is shown as what they are on.</summary>
    public bool IsPartParent(string geid) => _partParents.Contains(geid);

    /// <summary>The port each entity was last sighted on, and the entity last sighted on each port.</summary>
    private readonly Dictionary<string, string> _portOf = [];
    private readonly Dictionary<string, string> _occupant = [];

    /// <summary>
    /// A listing names every part of every item it lists, so a part of a listed item that the
    /// listing leaves out is no longer on it. Items the listing does not name — a rifle stored
    /// at a station — keep their parts: nothing says otherwise.
    /// </summary>
    private void DetachMissingParts(BodyEnumeration body)
    {
        foreach (var parent in _partParents)
        {
            if (!body.Geids.Contains(parent)) continue;
            if (!_contents.TryGetValue(new Holding(InventoryKind.Container, parent), out var byClass)) continue;

            foreach (var (itemClass, slot) in byClass)
            {
                foreach (var part in slot.Named.Where(p => p.Since < body.At && !body.Geids.Contains(p.Geid)).ToList())
                {
                    slot.Named.Remove(part);
                    SlotFor(LostTrack, itemClass, create: true)!.Named.Add(part);
                    _instanceAt[part.Geid] = LostTrack;
                    Stats.WornEvicted++;
                }
            }
        }
    }

    /// <summary>
    /// A new entity on a body port takes the place of the one last sighted there.
    /// <para>
    /// A port holds one item, so the old occupant is off the player, even when the burst that
    /// says so is not a full enumeration of the body. The respawn after a death in space is
    /// one: at 10:54:24 on 2026-09-25 the game re-attached the whole loadout under new geids
    /// with no body line, and the old geids went to the corpse. Only an occupant still on the
    /// player and still last seen on that port is replaced — one moved to the hands or stored
    /// since is not.
    /// </para>
    /// <para>
    /// Only the ports of the body itself count. Every other port name repeats: a magazine,
    /// optic or module port on each weapon and multitool, and <c>magazine_attach_1</c> both on
    /// armour and on a multitool's canister. The hands and the helmet hook hold whatever is
    /// being handled at the moment.
    /// </para>
    /// </summary>
    private void ReplaceOccupant(WornSighting sighting)
    {
        if (sighting.Port is not { } port || sighting.IsPart || !IsBodyPort(port)) return;

        if (_occupant.TryGetValue(port, out var old) && old != sighting.Geid
            && _portOf.GetValueOrDefault(old) == port
            && _instanceAt.GetValueOrDefault(old) == new Holding(InventoryKind.Equipped, "")
            && Find(old) is { } instance && instance.Since <= sighting.Timestamp)
        {
            SlotFor(new Holding(InventoryKind.Equipped, ""), instance.ItemClass, create: false)!.Named.Remove(instance);
            SlotFor(LostTrack, instance.ItemClass, create: true)!.Named.Add(instance);
            _instanceAt[old] = LostTrack;
            _carried.Remove(old);
            Stats.WornEvicted++;
        }

        _occupant[port] = sighting.Geid;
        _portOf[sighting.Geid] = port;
    }

    private static bool IsBodyPort(string port) =>
        port.StartsWith("Armor_", StringComparison.Ordinal)
        || port.StartsWith("wep_stocked_", StringComparison.Ordinal)
        || port is "backpack" or "wep_sidearm";

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
                NoteCarry(pick.Geid, move.ArrivedBy);
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
        if (taken < wanted && source.IsReal) taken += TakeFromLostTrack(move, target, wanted - taken);

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
                NoteCarry(pick.Geid, move.ArrivedBy);
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

    /// <summary>
    /// Draws units a class-level move's source could not supply from what the ledger lost
    /// track of: an entity a later enumeration no longer listed on the player went somewhere
    /// no line names, and a move of its class out of a real place is the first sign of where.
    /// Newest first, like everywhere else.
    /// </summary>
    private int TakeFromLostTrack(MoveRecord move, Holding target, int wanted)
    {
        if (SlotFor(LostTrack, move.ItemClass, create: false) is not { } slot) return 0;

        var taken = 0;
        while (taken < wanted && slot.Named.Count > 0)
        {
            var pick = slot.Named[^1];
            slot.Named.RemoveAt(slot.Named.Count - 1);
            NoteCarry(pick.Geid, move.ArrivedBy);
            PlaceInstance(
                pick.Geid, move.ItemClass, target, move.Timestamp, move.ArrivedBy,
                move.Succeeded, inferred: false, ambiguous: true);
            taken++;
        }

        Stats.UnitsFromLostTrack += taken;
        return taken;
    }

    /// <summary>
    /// Keeps <see cref="_carried"/> in step with every move that places a named instance, not
    /// only the moves that name it: a class-level move relocates named instances too, and one
    /// left flagged as carried would be used up by the next enumeration and kept back from
    /// the next update's relocation.
    /// </summary>
    private void NoteCarry(string geid, string arrivedBy)
    {
        if (arrivedBy == CarryArrival) _carried.Add(geid);
        else _carried.Remove(geid);
    }

    private IEnumerable<Holding> CarriedHoldings()
    {
        yield return new Holding(InventoryKind.Equipped, "");
        foreach (var key in _carriedContainers)
        {
            var holding = new Holding(InventoryKind.Container, key);
            if (_contents.ContainsKey(holding) && IsOnThePlayer(key)) yield return holding;
        }
    }

    /// <summary>
    /// Whether a carried-kind container is on the player now. A backpack is apparel whatever
    /// it holds, but one stored in a locker is not where a shortfall elsewhere was drawn from.
    /// With no move history it has never been seen to leave the player, so it counts.
    /// </summary>
    private bool IsOnThePlayer(string container)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var key = container;
        while (_instanceAt.TryGetValue(key, out var at))
        {
            if (at.Kind == InventoryKind.Equipped) return true;

            // Nested in another carried container: on the player only if that one is.
            if (at.Kind != InventoryKind.Container || !_carriedContainers.Contains(at.Key) || !seen.Add(at.Key))
            {
                return false;
            }

            key = at.Key;
        }

        return true;
    }

    private void CheckPlacement(string geid, MoveRecord move, Holding source)
    {
        if (!source.IsReal || !_instanceAt.TryGetValue(geid, out var believed)) return;

        // The ledger had no belief to check: it knew it had lost track of the item.
        if (believed == LostTrack) return;
        if (SlotFor(believed, move.ItemClass, create: false)?.Named.FirstOrDefault(i => i.Geid == geid) is not { } instance) return;

        Stats.PlacementChecks.Add(new PlacementCheck(believed, move.Timestamp - instance.Since)
        {
            Geid = geid,
            ItemClass = move.ItemClass,
            At = move.Timestamp,
            Actual = source,
            BelievedArrivedBy = instance.ArrivedBy,
            BelievedMovedByUpdate = instance.MovedByUpdate,
            SamePlace = PlaceOf(believed) == PlaceOf(source),
        });
    }

    /// <summary>Follows containers out to the holding they sit in, as far as the ledger knows.</summary>
    private Holding PlaceOf(Holding holding)
    {
        for (var hop = 0; hop < 6 && holding.Kind == InventoryKind.Container; hop++)
        {
            if (!_instanceAt.TryGetValue(holding.Key, out var outer)) break;
            holding = outer;
        }

        return holding;
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
