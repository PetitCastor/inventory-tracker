using System.Globalization;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Model;
using InventoryTracker.Naming;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Resolve;

/// <summary>
/// Turns the recorded move history into "what is sitting where", by replaying it.
/// <para>
/// There is no snapshot event in the logs — nothing ever enumerates an inventory's
/// contents — so every answer here is inference from a stream of deltas over an unknown
/// starting state. That has hard limits worth stating plainly: an item bought, looted or
/// claimed without a recorded move is invisible, and anything the server changes on its own
/// (a death, an insurance claim, a wipe, another player emptying a shared container) is
/// never logged at all. What this produces is a well-evidenced lower bound, not a manifest,
/// and every row carries a score and its caveats rather than being stated as fact.
/// </para>
/// </summary>
public sealed class HoldingResolver
{
    /// <summary>Containers nested deeper than this are a data error, not a real loadout.</summary>
    private const int MaxChainDepth = 6;

    /// <summary>
    /// Past this age a placement's age is worth mentioning. It no longer costs score: in the
    /// local corpus, age within one game build never contradicted a placement (every named
    /// move out of a place agreed with the ledger, up to 21 days). What did was a game update
    /// in between — see <see cref="GameUpdate"/>.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// When a new game build was first played, and which.
    /// <para>
    /// An update that comes with a character reset moves every stored item server-side, without
    /// a log line, to the home location the player picks while recreating their character: all
    /// 13 placements checked across build 12519617 had been moved to where the player first
    /// spawned afterwards — Lorville and Levski storage included. An update without one is not
    /// known to move anything.
    /// </para>
    /// </summary>
    /// <param name="Spawn">
    /// The location the player first arrived at in the new build, if the logs say. After a
    /// reset, the new home location.
    /// </param>
    /// <param name="LeftFrom">
    /// Where the player last was before the update, if the logs say.
    /// </param>
    /// <param name="ResetAt">
    /// When the character creator opened, if the player had to recreate their character before
    /// first spawning in this build; see <see cref="Ingest.CharacterCreatorOpened"/>.
    /// </param>
    public readonly record struct GameUpdate(
        DateTimeOffset At, int Build, string? Spawn = null, string? LeftFrom = null, DateTimeOffset? ResetAt = null)
    {
        /// <summary>The update came with a character reset.</summary>
        public bool CharacterReset => ResetAt is not null;
    }

    private readonly List<GameUpdate> _updates;

    /// <summary>
    /// How far to trust a placement made before each update, learned from the logs: the share
    /// of placements a later named move found where the ledger had them across it — the spawn
    /// station, for what the update was assumed to move there.
    /// </summary>
    private readonly Dictionary<int, UpdateSurvival> _survival;

    /// <summary>What the logs showed about placements surviving one update.</summary>
    public readonly record struct UpdateSurvival(int Checked, int Survived)
    {
        /// <summary>
        /// Below this many checks an update's own record is too thin to go on, and the
        /// default applies instead.
        /// </summary>
        private const int Enough = 5;

        /// <summary>
        /// What an unverified update costs: some are known to move items, this one might.
        /// </summary>
        private const double Unverified = 0.5;

        /// <summary>
        /// Never below this: even an update that moved everything checked leaves the item's
        /// last known place as the best lead there is.
        /// </summary>
        private const double Floor = 0.2;

        public bool Verified => Checked >= Enough;

        public double Factor => Verified ? Math.Clamp((double)Survived / Checked, Floor, 1.0) : Unverified;
    }

    /// <summary>Game updates, with what the logs showed about each.</summary>
    public IReadOnlyDictionary<int, UpdateSurvival> UpdateSurvivals => _survival;
    /// <summary>Game updates seen in the logs, oldest first.</summary>
    public IReadOnlyList<GameUpdate> Updates => _updates;

    /// <summary>A container's class plus the last place we saw the player open it. The class
    /// is null for a container the game never named — its capacity in µSCU, if a drag line
    /// ever reported one, is then all there is to describe it by.</summary>
    public sealed record ContainerInfo(
        string? ClassName, string? LastLocationId, long? Capacity);

    private readonly List<MoveRecord> _moves;
    private readonly Dictionary<string, string> _locationNames;
    private readonly Dictionary<string, ContainerInfo> _containers;

    /// <summary>Geids of containers ever seen worn on the body (backpack, legs, chest,
    /// undersuit). These are apparel that travels with the player, so they aggregate an item's
    /// moves but are never shown as a container — the chain skips straight through them.</summary>
    private readonly HashSet<string> _wornContainers;

    private readonly Dictionary<string, List<MoveRecord>> _byGeid;

    private readonly LedgerReplay _ledger;

    /// <summary>Ship classes and what the game calls them, from the wiki: "GLSN_Shiv" -> "Grey's Shiv".</summary>
    private readonly Dictionary<string, string> _shipNames;

    /// <summary>The newest retrieval of a ship of each class.</summary>
    private readonly Dictionary<string, (DateTimeOffset At, string? LocationId)> _retrievals;

    /// <summary>The moment ages are measured against: now, unless a replay study pins it.</summary>
    private readonly DateTimeOffset _asOf;

    /// <summary>What the replay did with every move, for measuring its reliability.</summary>
    public LedgerReplay.ReplayStats ReplayStats => _ledger.Stats;

    /// <summary>Systems and place names as a player would recognise them.</summary>
    public PlaceCatalog Places { get; }

    public IReadOnlyList<MoveRecord> Moves => _moves;

    private HoldingResolver(
        List<MoveRecord> moves,
        List<LedgerReplay.WornSighting> worn,
        List<LedgerReplay.BodyEnumeration> enumerations,
        Dictionary<string, string> locationNames,
        Dictionary<string, ContainerInfo> containers,
        HashSet<string> wornContainers,
        PlaceCatalog places,
        DateTimeOffset asOf,
        List<GameUpdate> updates,
        Dictionary<string, string> shipNames,
        Dictionary<string, (DateTimeOffset At, string? LocationId)> retrievals)
    {
        _shipNames = shipNames;
        _retrievals = retrievals;
        _asOf = asOf;
        _updates = updates;
        _moves = moves;
        _locationNames = locationNames;
        _containers = containers;
        _wornContainers = wornContainers;
        Places = places;

        _byGeid = moves
            .Where(m => m.ItemGeid is not null)
            .GroupBy(m => m.ItemGeid!)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Timestamp).ToList());

        // Apparel travels with the player, so the ledger draws on it when a move's source
        // falls short — the same test BuildChain uses to walk straight through it.
        var carried = containers
            .Where(kv => IsApparel(kv.Value.ClassName))
            .Select(kv => kv.Key)
            .Concat(wornContainers)
            .ToHashSet(StringComparer.Ordinal);

        // A character reset moves the player's belongings to the home location they chose, even
        // one at the station they had logged out at: storage elsewhere is emptied into it all the
        // same. An update without a reset is only weighed: nothing says it moved anything. In the
        // local corpus a spawn somewhere new without a reset (12572603, Orison after logging out
        // at ARC-L1) is just where the player was sent, not where their storage went.
        var relocations = updates
            .Where(u => u is { CharacterReset: true, Spawn: not null })
            .Select(u => new LedgerReplay.Relocation(u.At, u.Build, new Holding(InventoryKind.Location, u.Spawn!)));

        _ledger = LedgerReplay.Run(moves, worn, enumerations, carried, relocations);
        _survival = MeasureSurvival(_ledger.Stats.PlacementChecks, updates);
    }

    /// <summary>
    /// What each character reset moved, newest first, with places named as the player knows them.
    /// </summary>
    public IReadOnlyList<CharacterResetReport> CharacterResets => _characterResets ??= ReportResets();

    private IReadOnlyList<CharacterResetReport>? _characterResets;

    private List<CharacterResetReport> ReportResets() =>
        _ledger.Stats.Relocations
            .Select(r => new CharacterResetReport(
                _updates.First(u => u.Build == r.Build).ResetAt ?? r.At, r.Build, PlaceLabel(r.Destination.Key),
                r.From
                    .OrderByDescending(f => f.Value)
                    .Select(f => new CharacterResetOrigin(
                        f.Key.Kind == InventoryKind.Equipped ? "what you were wearing" : PlaceLabel(f.Key.Key), f.Value))
                    .ToList()))
            .OrderByDescending(r => r.ResetAt)
            .ToList();

    /// <summary>Whether two location ids are the same place, allowing for the ids the game re-issues.</summary>
    private static bool SamePlace(PlaceCatalog places, string a, string b) =>
        a == b || (places.ByLocationId(a)?.Id is { } pa && pa == places.ByLocationId(b)?.Id);

    /// <summary>
    /// Whether an update sent the player somewhere other than where they logged out. Not when
    /// the logs lack either end: an unknown logout place is no evidence of a move, and moving
    /// belongings on it would repeat the relocation to a parked ship that the spawn check exists
    /// to prevent.
    /// </summary>
    public bool MovedThePlayer(GameUpdate update) =>
        update is { Spawn: { } spawn, LeftFrom: { } leftFrom } && !SamePlace(Places, spawn, leftFrom);

    /// <summary>The whole move history is only a few thousand rows, so resolve in memory.</summary>
    /// <param name="asOf">
    /// When to measure staleness from. Defaults to now; a replay study passes the end of its
    /// corpus so its scores do not drift with the calendar.
    /// </param>
    public static HoldingResolver Load(TrackerDb db, DateTimeOffset? asOf = null) =>
        Load(db, asOf, afterMovesRead: null, readAdditionalSnapshot: null, beforeReplay: null);

    internal static HoldingResolver Load(
        TrackerDb db, DateTimeOffset? asOf, Action? afterMovesRead,
        Action<SqliteConnection>? readAdditionalSnapshot = null,
        Action<SqliteConnection>? beforeReplay = null)
    {
        using var cn = db.Open();
        var replayAsOf = asOf ?? DateTimeOffset.UtcNow;
        List<MoveRecord> moves;
        List<LedgerReplay.WornSighting> worn;
        List<LedgerReplay.BodyEnumeration> enumerations;
        Dictionary<string, string> locationNames;
        Dictionary<string, ContainerInfo> containers;
        HashSet<string> wornContainers;
        PlaceCatalog places;
        List<GameUpdate> updates;
        int? latestBuild;
        Dictionary<string, string> shipNames;
        Dictionary<string, (DateTimeOffset At, string? LocationId)> retrievals;

        // Deferred keeps this a reader until SQLite actually serves the first SELECT.
        // Materialize every table-backed input under that one generation, then dispose the
        // transaction before the in-memory ledger replay and scoring work.
        using (var transaction = cn.BeginTransaction(deferred: true))
        {
            (worn, enumerations) = LoadSightings(cn);
            moves = LoadMoves(cn);
            afterMovesRead?.Invoke();
            locationNames = LoadLocationNames(cn);
            containers = LoadContainers(cn);
            wornContainers = LoadWornContainers(cn);
            places = PlaceCatalog.Load(cn);
            (updates, latestBuild) = LoadUpdates(cn);
            shipNames = WikiVehicleCatalog.Load(cn);
            retrievals = LoadRetrievals(cn);
            readAdditionalSnapshot?.Invoke(cn);
            transaction.Commit();
        }

        beforeReplay?.Invoke(cn);
        return new HoldingResolver(
            moves, worn, enumerations, locationNames, containers, wornContainers, places,
            replayAsOf, updates, shipNames, retrievals) { LatestBuild = latestBuild };
    }

    /// <summary>The build of the newest session in the logs: the game as it is installed now.</summary>
    public int? LatestBuild { get; private init; }

    /// <summary>What to call a ship class: its in-game name when the wiki knows it, else the class.</summary>
    public string ShipLabel(string shipClass) => _shipNames.GetValueOrDefault(shipClass) ?? shipClass;

    /// <summary>
    /// Where and when a ship of this class last came out of storage, or null when the logs never
    /// saw one retrieved. Several ships of one class cannot be told apart by their lockers, so
    /// the newest retrieval of any of them stands for all.
    /// </summary>
    public ShipRetrieval? LastRetrieved(string shipClass) =>
        _retrievals.TryGetValue(shipClass, out var r)
            ? new ShipRetrieval(r.At, r.LocationId, r.LocationId is { } id ? PlaceLabel(id) : null)
            : null;

    /// <summary>
    /// Everything the ledger believes is currently held, by (place, item class): named entities
    /// individually, and unnamed units as a count per provenance group.
    /// </summary>
    public IReadOnlyList<ItemHolding> ResolveAll()
    {
        var rows = new List<ItemHolding>();

        foreach (var where in _ledger.Occupied)
        {
            foreach (var (itemClass, named, _) in _ledger.Contents(where))
            {
                foreach (var instance in named)
                {
                    rows.Add(Build(
                        where, itemClass, instance.Geid, quantity: 1, instance.Since, instance.ArrivedBy,
                        instance.Confirmed, instance.Inferred, instance.IdentityAmbiguous,
                        movedByUpdate: instance.MovedByUpdate));
                }

                // One row per group, so a row's caveats and score hold for every unit it counts:
                // ten units found at their source and one guess are not eleven guesses.
                foreach (var group in _ledger.LooseIn(where, itemClass))
                {
                    rows.Add(Build(
                        where, itemClass, geid: null, group.Quantity, group.Since, group.ArrivedBy,
                        group.Confirmed, group.Inferred, ambiguous: true, group.DuplicateRisk, group.MovedByUpdate));
                }
            }
        }

        return rows.OrderByDescending(h => h.LastMove).ToList();
    }

    /// <summary>Where one named entity ended up, or null if it was never seen.</summary>
    public ItemHolding? Resolve(string geid)
    {
        if (!_ledger.InstanceAt.TryGetValue(geid, out var where)) return null;

        var instance = _ledger.Find(geid);
        if (instance is null) return null;

        return Build(
            where, instance.ItemClass, geid, quantity: 1, instance.Since, instance.ArrivedBy,
            instance.Confirmed, instance.Inferred, instance.IdentityAmbiguous, movedByUpdate: instance.MovedByUpdate);
    }

    private string ClassOf(string geid) =>
        _byGeid.TryGetValue(geid, out var h) && h.Count > 0 ? h[^1].ItemClass : "";

    /// <summary>Full movement history for one instance, oldest first — the audit view.</summary>
    public IReadOnlyList<MoveRecord> History(string geid) =>
        _byGeid.TryGetValue(geid, out var h) ? h : [];

    private ItemHolding Build(
        Holding where, string itemClass, string? geid, int quantity, DateTimeOffset since,
        string arrivedBy, bool confirmed, bool inferred, bool ambiguous, bool duplicateRisk = false,
        int? movedByUpdate = null)
    {
        var caveats = new List<string>();
        var score = 1.0;

        if (!confirmed)
        {
            score *= 0.5;
            caveats.Add("the game never confirmed the move that put it here");
        }

        // An arrival nobody could source is only a problem when the same kind of item is still
        // recorded somewhere else: then one of the two is wrong. Otherwise it is simply the
        // first time the item turns up — looted, bought, or already there before the logs
        // begin — and it provably arrived, so it costs nothing.
        if (inferred && duplicateRisk)
        {
            score *= 0.8;
            caveats.Add("we saw this arrive but never saw where it came from, so it may have been counted twice");
        }
        else if (inferred)
        {
            caveats.Add("first seen being moved out of somewhere we had no record of — looted, bought, or there before the logs begin");
        }

        // Carried in hand is where eaten, drunk and handed-in items end up, and none of those
        // endings is ever logged. An item that is still here because nothing moved it on is as
        // likely gone as held; one stored back afterwards arrives by Store and is unaffected.
        if (arrivedBy == "Carry")
        {
            score *= 0.5;
            caveats.Add("last picked up in hand to use — food, drink and handed-in items are used up without a log line");
        }

        // Food and drink can be used straight out of a backpack, and that is never logged: three
        // cruz bottles stored in a backpack on 2026-08-23 and 08-25 were gone when the player
        // opened it on 2026-09-25, with no line in between. A consumable put down to be used
        // later is moved again within a day in every case in the logs (the longest, a medpen,
        // after 19 hours), so one untouched for a week is as likely gone as there. One lying
        // straight in a station's inventory is not doubted: every one taken out of a station
        // in the logs was taken out by a move line.
        // Carried in hand and dropped already say as much, and are scored for it below.
        if (IsConsumable(itemClass) && arrivedBy is not ("Carry" or "Drop")
            && where.Kind != InventoryKind.Location && _asOf - since > ConsumableShelf)
        {
            score *= 0.5;
            caveats.Add("food, drink and medical items are used up without a log line, and this one has not moved in " +
                        $"{(_asOf - since).TotalDays:F0} days — it may be gone");
        }

        if (arrivedBy == "Drop")
        {
            score *= 0.3;
            // A hangar keeps what is dropped in it, across a logout and a server change (two
            // items dropped at Keeger at 17:55 on 2026-10-12 were picked up after both at
            // 17:57). It shares the station's location id, so no line tells it from the rest
            // of the station, where the floor is cleared.
            caveats.Add("dropped on the floor — kept if in a hangar, cleared away anywhere else, and the logs do not say which");
        }

        // A game update played since this placement is the one thing observed to invalidate
        // one. A character reset moves stored items to the new home location and logs nothing;
        // the ledger has already moved the item there, so that move is known rather than
        // weighed. Any other update is weighed by how often the ledger's belief across it held;
        // across several, the least trustworthy one decides.
        var crossed = _updates.Where(u => u.At > since && u.At <= _asOf).ToList();
        if (crossed.Count > 0)
        {
            movedByUpdate ??= ContainerMovedByUpdate(where);

            // A reset either took the item to the home location or found it there already; both
            // are known. What happened before that no longer matters: wherever earlier updates
            // left it, the reset gathered it from there.
            // Not for what lies on the floor or in hand: the reset leaves those behind, and a floor
            // at the home station is cleared like any other.
            var root = RootOf(where);
            var followsTheReset = arrivedBy is not ("Drop" or "Carry");
            var settledBy = crossed.LastOrDefault(u =>
                u.CharacterReset && (u.Build == movedByUpdate
                                     || (followsTheReset && u.Spawn is { } home && root.Kind == InventoryKind.Location
                                         && SamePlace(Places, root.Key, home))));
            if (settledBy.CharacterReset)
            {
                if (settledBy.Build == movedByUpdate) caveats.Add(MovedCaveat(settledBy));
                crossed.RemoveAll(u => u.At <= settledBy.At);
            }

            if (crossed.Count > 0)
            {
                var worst = crossed.OrderBy(u => _survival[u.Build].Factor).First();
                score *= _survival[worst.Build].Factor;
                caveats.Add(UpdateCaveat(worst, _survival[worst.Build], MovedThePlayer(worst)));
            }
        }

        var age = _asOf - since;
        if (age > StaleAfter) caveats.Add($"last seen {age.TotalDays:F0} days ago");

        // Which of several identical items this is says nothing about whether one of them
        // is here, so it is reported without touching the score.
        if (ambiguous && geid is not null)
        {
            caveats.Add("the log did not name which of several identical items was moved, so this instance may be one of its twins");
        }

        var chain = BuildChain(where, geid, since, caveats, ref score);

        return new ItemHolding(
            geid, itemClass, quantity, chain, since, arrivedBy, score, ambiguous, caveats);
    }

    /// <summary>The holding the containers around <paramref name="where"/> sit in, as far as the ledger knows.</summary>
    private Holding RootOf(Holding where)
    {
        for (var hop = 0; hop < MaxChainDepth && where.Kind == InventoryKind.Container; hop++)
        {
            if (!_ledger.InstanceAt.TryGetValue(where.Key, out var outer)) break;
            where = outer;
        }

        return where;
    }

    /// <summary>
    /// The update that moved the container an item sits in, if one did: the item went with it.
    /// </summary>
    private int? ContainerMovedByUpdate(Holding where)
    {
        for (var hop = 0; hop < MaxChainDepth && where.Kind == InventoryKind.Container; hop++)
        {
            if (!_ledger.InstanceAt.TryGetValue(where.Key, out var outer)) return null;

            var container = _ledger.NamedIn(outer, ClassOf(where.Key)).FirstOrDefault(i => i.Geid == where.Key);
            if (container?.MovedByUpdate is { } build) return build;
            where = outer;
        }

        return null;
    }

    private static string MovedCaveat(GameUpdate reset) =>
        $"moved here by the character reset of {reset.ResetAt:yyyy-MM-dd} (game build {reset.Build}): the game moves " +
        "every stored item to the new home location, and logs none of it";

    private static string UpdateCaveat(GameUpdate update, UpdateSurvival record, bool movedThePlayer) =>
        record.Verified
            ? $"placed before the game update to build {update.Build}, after which {record.Checked - record.Survived} " +
              $"of {record.Checked} items checked were not where the ledger had them"
            : update.CharacterReset
                ? update.Spawn is null
                    ? $"placed before the character reset of {update.ResetAt:yyyy-MM-dd} (game build {update.Build}), " +
                      "which moved stored items to a new home location the logs do not name"
                    : $"placed before the character reset of {update.ResetAt:yyyy-MM-dd} (game build {update.Build}), " +
                      "which moved stored items to the new home location — this was not one of them"
            : update.Spawn is null
                ? $"placed before the game update to build {update.Build}, which came without a character reset — " +
                  "the logs do not say where the player spawned after it, and no later move has checked it"
            : movedThePlayer
                ? $"placed before the game update to build {update.Build}, which came without a character reset — " +
                  "the player spawned somewhere new, and no later move has checked whether stored items stayed put"
            : update.LeftFrom is null
                ? $"placed before the game update to build {update.Build}, which no later move has checked — the " +
                  "logs do not say where the player logged out before it, so nothing says whether it moved anything"
                : $"placed before the game update to build {update.Build}, which no later move has checked — the " +
                  "player picked up where they had logged out, so nothing says whether it moved anything";

    /// <summary>
    /// Walks from where the item sits outward: an item is in a backpack, and the backpack is
    /// itself an entity whose own last move says which station it is at.
    /// </summary>
    /// <param name="since">When the item arrived where it sits: for an item the ledger lost
    /// track of, the last time it was seen on the player.</param>
    private List<HoldingLink> BuildChain(
        Holding start, string? itemGeid, DateTimeOffset since, List<string> caveats, ref double score)
    {
        var chain = new List<HoldingLink>();
        var seen = new HashSet<string>();
        if (itemGeid is not null) seen.Add(itemGeid);

        // Set once the walk steps through a worn container, so a later Equipped hop can be
        // labelled correctly: the backpack is worn, not the item inside it, so ending up here
        // by way of apparel reads as "carried" rather than "equipped".
        var passedWorn = false;

        // The last container walked through, whose own placement the walk is following.
        string? passedContainer = null;

        var kind = start.Kind;
        var key = start.Key;

        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            switch (kind)
            {
                case InventoryKind.Location:
                {
                    var place = Places.ByLocationId(key);
                    if (place is not null)
                    {
                        chain.Add(new HoldingLink(
                            kind, key, place.NameConfirmed ? place.Label : $"{place.Label} (unconfirmed)"));

                        // Where the item is does not depend on the label, so it costs nothing,
                        // but the label is our guess and says so.
                        if (place.QuantumPoint is { } point)
                        {
                            caveats.Add(
                                $"this place is named after the quantum destination the player arrived at ({point}), " +
                                "not by the game");
                        }
                    }
                    else if (_locationNames.TryGetValue(key, out var raw))
                    {
                        chain.Add(new HoldingLink(kind, key, raw));
                    }
                    else
                    {
                        score *= 0.7;
                        caveats.Add("this station's id was never paired with a name");
                        chain.Add(new HoldingLink(kind, key, $"location {key}"));
                    }
                    return chain;
                }

                case InventoryKind.Equipped:
                    chain.Add(new HoldingLink(
                        kind, "", passedWorn ? "carried on your character" : "equipped on your character"));
                    return chain;

                // The key is the location the item was dropped in, when the logs said.
                case InventoryKind.World:
                    chain.Add(new HoldingLink(kind, key, key.Length == 0
                        ? "dropped in the world"
                        : $"dropped at {PlaceLabel(key)}"));
                    return chain;

                // A later login no longer listed it on the player, and no line said where it
                // went. The last time it was seen on the player is the only lead there is: the
                // item's own arrival, or, inside a backpack, the backpack's.
                case InventoryKind.LostTrack:
                {
                    var lastOnPlayer = (passedContainer is { } outer ? _ledger.Find(outer)?.Since : null) ?? since;
                    score *= 0.3;
                    caveats.Add(
                        $"last seen on your character {lastOnPlayer:yyyy-MM-dd}; a later login no longer listed it, " +
                        "and no log line says where it went");
                    chain.Add(new HoldingLink(kind, "", "no longer on your character — whereabouts unknown"));
                    return chain;
                }

                case InventoryKind.Container when _ledger.IsPartParent(key) && _ledger.Find(key) is { } parent:
                {
                    // A part on the item it belongs to. The attachment line that put it there is
                    // proof, so the hop costs nothing; it is shown so a visor reads as on its
                    // helmet rather than loose on the character.
                    chain.Add(new HoldingLink(kind, key, $"on {parent.ItemClass}"));
                    if (!seen.Add(key))
                    {
                        caveats.Add("container nesting loops back on itself");
                        return chain;
                    }

                    // Find only returns an entity the ledger has placed.
                    passedContainer = key;
                    var holdingTheParent = _ledger.InstanceAt[key];
                    kind = holdingTheParent.Kind;
                    key = holdingTheParent.Key;
                    continue;
                }

                case InventoryKind.Container:
                {
                    var info = _containers.GetValueOrDefault(key);

                    // A ship's locker goes wherever the ship is flown, so it ends the chain on the
                    // ship rather than on the station it was last opened at. Its contents are as
                    // certain as any container's: the locker kept its geid and what was in it
                    // through a store and a retrieve of the ship.
                    if (ShipStorage.TryShipClass(info?.ClassName, out var shipClass))
                    {
                        chain.Add(new HoldingLink(InventoryKind.Ship, shipClass, $"in your {ShipLabel(shipClass)}"));
                        return chain;
                    }

                    var label = info?.ClassName ?? SizeLabel(info?.Capacity, key);
                    var lootable = IsLootable(info?.ClassName);

                    // Worn apparel (backpack, chest, legs, arms, undersuit) is not a place — it
                    // travels with the player. Its contents aggregate an item's moves, but it
                    // is never shown as a container: emit no link and walk straight through it
                    // to wherever the apparel itself ended up. It is recognised by class name —
                    // the reliable signal, since apparel is used for nested storage far more
                    // often than it is seen worn — with an attachment sighting as a backstop.
                    var worn = IsApparel(info?.ClassName) || _wornContainers.Contains(key);
                    if (!worn) chain.Add(new HoldingLink(kind, key, label));
                    else passedWorn = true;

                    // A container that contains itself would loop forever.
                    if (!seen.Add(key))
                    {
                        caveats.Add("container nesting loops back on itself");
                        return chain;
                    }

                    // Every extra hop is another inference stacked on the last one. A skipped
                    // worn hop is not a shown inference, and an attachment sighting is
                    // authoritative, so it costs nothing.
                    if (depth > 0 && !worn) score *= 0.9;

                    // The container is an entity too, so the ledger knows where it went.
                    if (_ledger.InstanceAt.TryGetValue(key, out var holdingTheContainer))
                    {
                        passedContainer = key;
                        kind = holdingTheContainer.Kind;
                        key = holdingTheContainer.Key;
                        continue;
                    }

                    // Boxes and crates get set down rather than stored, so they often have
                    // no move history at all. Opening one still proves it was within arm's
                    // reach, which pins it to wherever the player was standing — but a
                    // lootable container is found, not owned, and is exactly as likely to
                    // be opened out in the field as at a real station. Pinning those spawns
                    // a "place" for every wreck and crate ever looted, most never named by
                    // the game, which is what fills the browse list with "Unknown system".
                    // Lootable containers are therefore never tracked as a place at all.
                    // Worn apparel gets no such fallback either: where the player last opened
                    // their own backpack is just wherever they happened to be standing at the
                    // time, not where the backpack — or anything in it — actually lives.
                    if (!worn && !lootable && info?.LastLocationId is { } seenAt)
                    {
                        score *= 0.8;
                        caveats.Add($"'{label}' was placed by where you last opened it, not by a recorded move");
                        kind = InventoryKind.Location;
                        key = seenAt;
                        continue;
                    }

                    // A worn container with no ledger placement of its own — never stored,
                    // never seen equipped — has nothing real to point to, and unlike a box or
                    // crate it gets no opened-here fallback either. The obvious default is that
                    // it is still on the player, so say that plainly with a moderate penalty
                    // rather than either asserting a place or dropping the item's location
                    // entirely.
                    if (worn)
                    {
                        score *= 0.8;
                        caveats.Add(
                            "in worn apparel that was never seen being stored anywhere, so assumed still carried");
                        chain.Add(new HoldingLink(InventoryKind.Equipped, "", "carried on your character"));
                        return chain;
                    }

                    score *= 0.6;
                    caveats.Add(lootable
                        ? $"'{label}' is a lootable container, not a tracked place"
                        : $"we have never seen where '{label}' itself is kept");
                    return chain;
                }

                default:
                    return chain;
            }
        }

        caveats.Add("container nesting was deeper than we follow");
        return chain;
    }

    /// <summary>What to call a location id, as plainly as the catalogue and the logs allow.</summary>
    public string PlaceLabel(string locationId) =>
        Places.ByLocationId(locationId) is { } place
            ? place.NameConfirmed ? place.Label : $"{place.Label} (unconfirmed)"
            : _locationNames.GetValueOrDefault(locationId) ?? $"location {locationId}";

    /// <summary>The newest retrieval per ship class, from ships whose class a pilot seat named.</summary>
    private static Dictionary<string, (DateTimeOffset At, string? LocationId)> LoadRetrievals(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT class_name, retrieved_ts, retrieved_loc_id FROM vehicle
            WHERE class_name IS NOT NULL AND retrieved_ts IS NOT NULL
            ORDER BY retrieved_ts
            """;

        var map = new Dictionary<string, (DateTimeOffset, string?)>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = (
                DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.IsDBNull(2) ? null : r.GetString(2));
        }
        return map;
    }

    private static List<MoveRecord> LoadMoves(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, move_type, item_class, item_geid, amount,
                   src_kind, src_key, tgt_kind, tgt_key, tgt_raw, result, action
            FROM move
            ORDER BY ts, id
            """;

        var rows = new List<MoveRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new MoveRecord(
                r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5),
                ParseKind(r.GetString(6)),
                r.GetString(7),
                ParseKind(r.GetString(8)),
                r.GetString(9),
                r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : r.GetString(12)));
        }
        return rows;
    }

    /// <summary>
    /// The player's body entity sits on this port, and it is re-sent at the head of every
    /// enumeration of what the player has on them — on spawn, on a server switch, on a
    /// respawn — so each of its sightings dates one complete listing.
    /// </summary>
    private const string BodyPort = "Body_ItemPort";

    /// <summary>
    /// How long after the body line an enumeration's other lines keep arriving. They follow
    /// it within two milliseconds; the margin only has to cover a slow stream-in.
    /// </summary>
    private static readonly TimeSpan EnumerationSpread = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every attachment sighting, and every complete listing of the body they make up.
    /// <para>
    /// Each sighting puts its item on the player at that moment. Each listing — everything
    /// sighted from a body line on, within <see cref="EnumerationSpread"/> — is authoritative
    /// for the whole body: what the ledger had on the player before it and it leaves out is
    /// no longer there. The login placeholder set is never read: it is not the player's. Nor
    /// is the character itself (<see cref="AttachmentSeen.IsCharacterPort"/>).
    /// </para>
    /// </summary>
    private static (List<LedgerReplay.WornSighting> Worn, List<LedgerReplay.BodyEnumeration> Enumerations)
        LoadSightings(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, ts, geid, class_name, port, parent_geid FROM attachment_sighting
            WHERE placeholder = 0
            ORDER BY session_id, line_offset
            """;

        var rows = new List<(long Session, LedgerReplay.WornSighting Sighting, string Port)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                rows.Add((
                    r.GetInt64(0),
                    new LedgerReplay.WornSighting(
                        DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2), r.GetString(3),
                        r.GetString(4), AttachmentSeen.IsPartPort(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5)),
                    r.GetString(4)));
            }
        }

        var enumerations = new List<LedgerReplay.BodyEnumeration>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Port != BodyPort) continue;

            // Log order within the session, filtered by time rather than cut at the first line
            // out of range: a line stamped out of order must neither stop the listing short nor
            // drag in an earlier burst. rows is a List, so Skip does not re-walk it.
            var at = rows[i].Sighting.Timestamp;
            var listed = rows.Skip(i)
                .TakeWhile(x => x.Session == rows[i].Session)
                .Select(x => x.Sighting)
                .Where(w => w.Timestamp >= at && w.Timestamp - at <= EnumerationSpread)
                .ToList();

            enumerations.Add(new LedgerReplay.BodyEnumeration(
                at,
                listed.Select(w => w.Geid).ToHashSet(StringComparer.Ordinal),
                listed.Select(w => w.ItemClass).ToHashSet(StringComparer.Ordinal)));
        }

        // The character's own parts still count above (the body line is what dates a listing)
        // but are never handed on as something worn: they are not items the player owns.
        return (rows.Where(x => !AttachmentSeen.IsCharacterPort(x.Port)).Select(x => x.Sighting).ToList(), enumerations);
    }

    /// <summary>
    /// Whether a container class is body-worn apparel — a backpack or an armour slot (core for
    /// the chest, legs, arms) or the undersuit. These carry storage but are worn on the
    /// character, so they aggregate an item's moves without ever being shown as a place. The
    /// world's own containers (freight elevators, lootable crates, carryable inventory boxes)
    /// match none of these and keep their chain hop.
    /// </summary>
    private static bool IsApparel(string? className)
    {
        if (className is null) return false;

        foreach (var mark in ApparelMarks)
        {
            if (className.Contains(mark, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>How long a consumable can sit untouched before it is doubted.</summary>
    private static readonly TimeSpan ConsumableShelf = TimeSpan.FromDays(7);

    /// <summary>
    /// Food, drink and CureLife medical consumables (medpens are
    /// <c>crlf_consumable_healing_01</c>). Not every <c>_consumable_</c> class: a mission hard
    /// drive is <c>FPS_Consumable_HardDrive_Generic</c>, and it does not get used up.
    /// </summary>
    private static bool IsConsumable(string itemClass) =>
        itemClass.StartsWith("Drink_", StringComparison.OrdinalIgnoreCase)
        || itemClass.StartsWith("Food_", StringComparison.OrdinalIgnoreCase)
        || itemClass.StartsWith("crlf_consumable_", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ApparelMarks =
        ["backpack", "undersuit", "_core_", "_legs_", "_arms_"];

    /// <summary>One SCU as the logs count it. Capacities are written in µSCU.</summary>
    private const double MicroScuPerScu = 1_000_000;

    /// <summary>
    /// What to call a container the game never named. Its capacity is the one thing ever
    /// said about it, and it is also how the player thinks of the thing they set down —
    /// a 2 SCU box. Without even that, the geid is all that distinguishes it.
    /// </summary>
    private static string SizeLabel(long? capacity, string key)
    {
        if (capacity is not > 0) return $"container {key}";

        var scu = capacity.Value / MicroScuPerScu;
        return scu >= 1
            ? $"{scu:0.##} SCU container"
            : $"{capacity.Value / (MicroScuPerScu / 100):0.##} cSCU container";
    }

    /// <summary>
    /// Whether a container class is one of the game's found-loot crates (class names like
    /// <c>Lootable_Container_...</c>) rather than storage the player actually owns. These are
    /// scattered through the world and opened once in passing, so unlike a carryable box or a
    /// freight elevator, their position is not worth remembering as a place.
    /// </summary>
    private static bool IsLootable(string? className) =>
        className is not null && className.Contains("lootable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For each update, the placements a later named move checked across it, and how many
    /// the game confirmed still in place. A check spans every update between the placement
    /// and the move.
    /// </summary>
    private static Dictionary<int, UpdateSurvival> MeasureSurvival(
        IReadOnlyList<LedgerReplay.PlacementCheck> checks, IReadOnlyList<GameUpdate> updates)
    {
        var survival = new Dictionary<int, UpdateSurvival>();
        foreach (var update in updates)
        {
            var across = checks.Where(c => update.At > c.At - c.Age && update.At <= c.At).ToList();
            survival[update.Build] = new UpdateSurvival(across.Count, across.Count(c => c.SamePlace));
        }

        return survival;
    }

    /// <summary>
    /// Each build's first appearance, keeping only builds newer than every one before — a
    /// rotated backup ingested late must not read as a downgrade followed by an update — with
    /// the first place the player arrived at in that build, and whether they had to recreate
    /// their character before getting there. Also the newest build played at all.
    /// <para>
    /// A reset counts for the update only when it comes before the build's first spawn: that is
    /// where a patch puts it. One the player asks for later in a build has no update to hang
    /// off, and is not tracked.
    /// </para>
    /// </summary>
    private static (List<GameUpdate> Updates, int? LatestBuild) LoadUpdates(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT build, COALESCE(first_ts, last_line_ts) AS since, first_loc_id, cur_loc_id, char_reset_at
            FROM session
            WHERE build IS NOT NULL AND COALESCE(first_ts, last_line_ts) IS NOT NULL
            ORDER BY since
            """;

        var firstPlayed = new List<(int Build, DateTimeOffset At, string? LeftFrom)>();
        var spawn = new Dictionary<int, string>();
        var reset = new Dictionary<int, DateTimeOffset>();
        string? lastSeen = null;
        int? latest = null;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var build = r.GetInt32(0);
                if (!firstPlayed.Exists(b => b.Build == build))
                {
                    firstPlayed.Add((build, DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), lastSeen));
                }

                // Before this session's own spawn is taken: a reset session reaches its first
                // location only after the creator, so the build has no spawn yet when it is read.
                if (!r.IsDBNull(4) && !spawn.ContainsKey(build))
                {
                    reset.TryAdd(build, DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture));
                }

                // A session that never reached a location — a crash at the menu, say — says
                // nothing; the build's next one still spawned after the update.
                if (!r.IsDBNull(2)) spawn.TryAdd(build, r.GetString(2));
                if (!r.IsDBNull(3)) lastSeen = r.GetString(3);
                latest = build;
            }
        }

        var updates = new List<GameUpdate>();
        int? newest = null;
        foreach (var (build, at, leftFrom) in firstPlayed)
        {
            if (newest is not null && build > newest)
            {
                updates.Add(new GameUpdate(
                    at, build, spawn.GetValueOrDefault(build), leftFrom,
                    reset.TryGetValue(build, out var resetAt) ? resetAt : null));
            }

            if (newest is null || build > newest) newest = build;
        }

        return (updates, latest);
    }

    private static InventoryKind ParseKind(string s) =>
        Enum.TryParse<InventoryKind>(s, out var k) ? k : InventoryKind.Unknown;

    private static Dictionary<string, string> LoadLocationNames(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT location_id, name FROM location_name";

        var map = new Dictionary<string, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    private static Dictionary<string, ContainerInfo> LoadContainers(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT geid, class_name, last_loc_id, capacity FROM container_class";

        var map = new Dictionary<string, ContainerInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = new ContainerInfo(
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt64(3));
        }
        return map;
    }

    /// <summary>
    /// Every container geid ever enumerated on the player's body: a backpack worn last week and
    /// stored today is still apparel and must never be shown as a container. Membership in the
    /// attachment table is the signal — world crates and freight elevators never appear there.
    /// </summary>
    private static HashSet<string> LoadWornContainers(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT geid FROM attachment WHERE port IS NOT NULL";

        var set = new HashSet<string>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }
}
