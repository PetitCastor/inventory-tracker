using System.Globalization;
using InventoryTracker.Data;
using InventoryTracker.Model;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Ingest;

public sealed record IngestStats
{
    public int FilesSeen { get; set; }
    public int FilesParsed { get; set; }
    public int LinesRead { get; set; }
    public int MovesInserted { get; set; }
    public int CompletionsApplied { get; set; }
    public int ContainersIdentified { get; set; }
    public int CapacitiesSeen { get; set; }
    public int LocationsNamed { get; set; }
    public int LocationConflicts { get; set; }
    public int GeidsRecovered { get; set; }
    public int BatchMovesExpanded { get; set; }
    public int AttachmentsSeen { get; set; }

    /// <summary>Requests the server never processed before the connection closed.</summary>
    public int RequestsLost { get; set; }
    public int PlaceEvidence { get; set; }

    /// <summary>
    /// Lines that carried every visible sign of being an inventory move request but matched
    /// none of <see cref="InventoryEventParser"/>'s patterns — the canary for a log-grammar
    /// change the parser has not been taught yet. Should always be zero; see
    /// <see cref="UnrecognisedMove"/>.
    /// </summary>
    public int UnrecognisedMoves { get; set; }
}

/// <summary>
/// Reads Game.log files into the SQLite store. Ingestion is resumable (per-file byte
/// watermark) and idempotent (moves keyed by their line offset), so it can be run
/// repeatedly and continuously against a growing live log.
/// </summary>
public sealed class LogIngestor(TrackerDb db, string logDir, DateTimeOffset? fromDate = null)
{
    /// <summary>
    /// Everything before this is ignored, on top of <see cref="InventoryEventParser.Cutoff"/>.
    /// Lets a user mark an inception date and disregard history from before it.
    /// </summary>
    private readonly DateTimeOffset _sinceTs = fromDate is { } f && f > InventoryEventParser.Cutoff
        ? f : InventoryEventParser.Cutoff;

    /// <summary>
    /// A location id is bound to the name in the next RequestLocationInventory. Beyond
    /// this gap the two lines are unrelated and pairing them would invent a mapping.
    /// </summary>
    private static readonly TimeSpan LocationPairingWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after arriving somewhere a streamed asset path still describes that place.
    /// The station's own object containers load on approach; anything much later is just
    /// scenery the player flew past without the location id changing.
    /// </summary>
    private static readonly TimeSpan AssetAttributionWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far apart a move and the entity spawn that fulfils it can be before sharing a
    /// request number stops meaning they are the same operation. Request numbers restart
    /// per file and again on every shard change.
    /// </summary>
    private static readonly TimeSpan GeidPairingWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far apart a move and a completion line can be before sharing a request number
    /// stops meaning they are the same operation. Completions normally land within
    /// milliseconds; the allowance is for a slow server, not for a reused number.
    /// <para>
    /// Without this, a move restored by <see cref="PreloadPendingRequests"/> keeps its slot
    /// until some newer move reuses the number, so a completion arriving first would stamp
    /// its result onto an unrelated row from much earlier in the file.
    /// </para>
    /// </summary>
    private static readonly TimeSpan CompletionPairingWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many unresolved moves to restore when resuming mid-file. Completions past this
    /// go unmatched, which costs a confirmation flag rather than a placement.
    /// </summary>
    private const int PreloadLimit = 500;

    public string LogDir { get; } = logDir;

    /// <summary>The inception date this ingestor was built with.</summary>
    public DateTimeOffset? FromDate { get; } = fromDate;

    /// <summary>
    /// Parse state for the live file, carried from one pass to the next.
    /// <para>
    /// The watcher runs a pass every second or so while the game is writing, and most of
    /// what correlates lines — where the player is standing, the caller an Add-move line
    /// stashed for its Queued half, the entity spawn that lands just after a completion —
    /// spans more than one pass. Rebuilding it from scratch each time left 12 of 15
    /// containers in a real session unplaced and lost geids at pass boundaries.
    /// </para>
    /// <para>
    /// Keyed by session and tagged with the offset it is valid at: a state is only reused
    /// by the pass that resumes exactly where it stopped. Any other resume — the first pass
    /// after launch, a rotation, a pass that failed and rolled back — rebuilds it from the
    /// store instead.
    /// </para>
    /// </summary>
    private readonly Dictionary<long, (long Offset, SessionState State)> _carried = [];

    /// <summary>Parses everything not yet ingested. Safe to call on a timer.</summary>
    public IngestStats IngestAll(CancellationToken ct = default)
    {
        var stats = new IngestStats();
        var files = LogFileLocator.Discover(LogDir, _sinceTs);
        stats.FilesSeen = files.Count;

        using var cn = db.Open();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            IngestFile(cn, file, stats);
        }

        return stats;
    }

    /// <summary>Parses one file from its stored watermark to the last complete line.</summary>
    public void IngestFile(SqliteConnection cn, LogFile file, IngestStats stats)
    {
        var session = LoadOrCreateSession(cn, file.Path);

        // Rotated backups never change again, so once fully read they are never reopened.
        if (session.Complete)
        {
            _carried.Remove(session.Id);
            return;
        }

        session = DiscardIfRotated(cn, session, file);

        var reader = new ByteLineReader(session.Offset);
        var state = TakeState(cn, session);
        var restarted = false;

        using var tx = cn.BeginTransaction();

        var parsedAny = false;
        DateTimeOffset? firstTs = null, lastTs = null, newestLineTs = null;

        // Isolates this pass's contribution so it can be added to the session's running
        // total below, rather than overwriting it — the counter must survive across every
        // ingest pass over a live, growing file.
        var unrecognisedBefore = stats.UnrecognisedMoves;

        foreach (var line in reader.ReadLines(file.Path))
        {
            stats.LinesRead++;

            // The reader starts over when the file shrank under it; whatever the state knew
            // about the old content no longer describes this one.
            if (!restarted && line.StartOffset < session.Offset)
            {
                state = new SessionState();
                restarted = true;
            }

            if (!LogLine.TryParse(line.Text, out var log)) continue;

            // Captured before the cutoff filter below: this is the file's identity for
            // DiscardIfRotated, which compares it against LogFileLocator.FirstTimestamp
            // (also unfiltered). Filtering it by _sinceTs would make a session whose
            // inception date falls after the file's true start look rotated forever.
            firstTs ??= log.Timestamp;

            // Tracked independently of _sinceTs: a file that predates the inception date is
            // otherwise indistinguishable from an empty one, when what the user needs to see
            // is "your newest log line is from before your inception date".
            if (newestLineTs is null || log.Timestamp > newestLineTs) newestLineTs = log.Timestamp;

            if (log.Timestamp < _sinceTs) continue;

            lastTs = log.Timestamp;
            parsedAny = true;

            var ev = InventoryEventParser.Parse(log);
            if (ev is null) continue;

            Apply(cn, tx, session.Id, line.StartOffset, ev, state, stats);
        }

        // A rotated file is finished: nothing still waiting in it will ever be processed.
        if (!file.IsLive) stats.RequestsLost += VoidUnprocessed(cn, tx, session.Id, before: null);

        var unrecognisedThisPass = stats.UnrecognisedMoves - unrecognisedBefore;
        SaveSession(
            cn, tx, session.Id, reader.Offset, firstTs, lastTs, newestLineTs, unrecognisedThisPass,
            state.CurrentLocation, state.FirstLocation, session.Build ?? LogFileLocator.BuildOf(file.Path),
            complete: !file.IsLive);
        tx.Commit();

        // Only once committed: a pass that throws leaves no entry behind, so the next one
        // rebuilds from what the store actually holds rather than from half-applied state.
        if (file.IsLive) _carried[session.Id] = (reader.Offset, state);

        if (parsedAny) stats.FilesParsed++;
    }

    /// <summary>
    /// The state to resume <paramref name="session"/> with: the one the previous pass left,
    /// if it stopped exactly here, otherwise as much as the store can restore.
    /// </summary>
    private SessionState TakeState(SqliteConnection cn, SessionRow session)
    {
        // Removed rather than read, so a pass that fails part-way cannot hand its
        // half-updated state to the next one.
        if (_carried.Remove(session.Id, out var carried) && carried.Offset == session.Offset && session.Offset > 0)
        {
            return carried.State;
        }

        var state = new SessionState { CurrentLocation = session.CurrentLocation };
        PreloadPendingRequests(cn, session.Id, state);
        PreloadLastBurst(cn, session.Id, state);
        return state;
    }

    private void Apply(
        SqliteConnection cn,
        SqliteTransaction tx,
        long sessionId,
        long lineOffset,
        InventoryEvent ev,
        SessionState state,
        IngestStats stats)
    {
        switch (ev)
        {
            // Arrives ~1ms *before* the matching ItemMoved, so stash the caller for it.
            case MoveRequested req:
                state.PendingCallers[req.RequestNo] = req.Caller;

                // Drop emits no Queued line, so this is the only record that the item
                // left an inventory and became a loose world entity.
                if (req.MoveType == "Drop")
                {
                    var dropped = new ItemMoved(
                        req.Timestamp, req.RequestNo, Player: "", PlayerId: "", req.MoveType,
                        req.SourceInventory, InventoryRef.World, req.ItemClass, ItemGeid: null,
                        Amount: 1);
                    Record(cn, tx, sessionId, lineOffset, dropped, req.Caller, state, stats);

                    // Nothing else will consume this caller, and leaving it behind lets a
                    // later request that reuses the number inherit an AttachItem caller,
                    // which would turn its INVALID target into a fabricated equip.
                    state.PendingCallers.Remove(req.RequestNo);
                }
                break;

            // A multi-select drag: this line is the batch's only record, so fan it out.
            case MoveBatchRequested batch:
            {
                for (var i = 0; i < batch.ItemClasses.Count; i++)
                {
                    var one = new ItemMoved(
                        batch.Timestamp, batch.RequestNo, Player: "", PlayerId: "", batch.MoveType,
                        batch.Source, batch.Target, batch.ItemClasses[i], ItemGeid: null, Amount: 1);

                    var normalized = InventoryEventParser.Normalize(one, batch.Caller);
                    if (normalized is null) continue;

                    Record(cn, tx, sessionId, lineOffset, normalized, batch.Caller, state, stats, itemIx: i);
                }

                stats.BatchMovesExpanded++;
                break;
            }

            case ItemMoved move:
            {
                var caller = state.PendingCallers.GetValueOrDefault(move.RequestNo);
                state.PendingCallers.Remove(move.RequestNo);

                var normalized = InventoryEventParser.Normalize(move, caller);
                if (normalized is null) break; // no real destination — tells us nothing

                Record(cn, tx, sessionId, lineOffset, normalized, caller, state, stats);
                break;
            }

            // An entity spawning names the instance behind the class-level move that asked
            // for it. The two share a request number and nothing else.
            case EntitySpawned spawn:
            {
                state.PendingGeids[spawn.RequestNo] = (spawn.Geid, spawn.ClassName, spawn.Timestamp);

                if (state.MoveByRequest.TryGetValue(spawn.RequestNo, out var pending))
                {
                    foreach (var m in pending)
                    {
                        if (spawn.Timestamp - m.At > GeidPairingWindow) continue;
                        if (ApplyGeid(cn, tx, m.Id, spawn.Geid, spawn.ClassName)) stats.GeidsRecovered++;
                    }
                }
                break;
            }

            // An instance-level arrival that does not go through the request handshake.
            // Recording it alongside the Queued line is harmless: placing a known entity
            // in the same holding twice is the same fact, not two of them.
            case ItemStored stored:
            {
                var synthetic = new ItemMoved(
                    stored.Timestamp, RequestNo: 0, Player: "", PlayerId: "", MoveType: "StoreItem",
                    InventoryRef.Invalid, stored.Target, stored.ClassName, stored.Geid, Amount: 1);

                InsertMove(cn, tx, sessionId, lineOffset, synthetic, caller: null, itemIx: 0);
                TouchContainer(cn, tx, stored.Target, stored.Timestamp, state.CurrentLocation);
                break;
            }

            case AttachmentSeen worn:
                // A local line is the client predicting an equip still in flight; the
                // persistent line for the real entity follows within seconds.
                if (!worn.IsPersistent) break;

                RecordSighting(cn, tx, sessionId, lineOffset, worn, state);
                if (!worn.IsPlaceholder) UpsertAttachment(cn, tx, worn);
                stats.AttachmentsSeen++;
                break;

            // The entry is deliberately left in place: the entity spawn that names the
            // instance arrives *after* the completion, and dropping the mapping here would
            // lose the only chance to bind them. The next move to reuse the number clears it.
            case MoveCompleted done:
                if (state.MoveByRequest.TryGetValue(done.RequestNo, out var moves))
                {
                    var applied = 0;
                    foreach (var m in moves)
                    {
                        // A number alone does not bind a completion to a move: they restart
                        // per file and again on every shard change.
                        if (done.Timestamp - m.At > CompletionPairingWindow) continue;

                        UpdateResult(cn, tx, m.Id, done.Result);
                        applied++;
                    }

                    if (applied > 0) stats.CompletionsApplied++;
                }
                break;

            case ContainerIdentified container:
                UpsertContainer(cn, tx, container, state.CurrentLocation);
                stats.ContainersIdentified++;
                break;

            // The canary firing: a request-shaped line neither ParseQueued nor ParseAddMove
            // could read. Nothing to persist for it beyond the count — there is no move to
            // record, only the fact that one was probably missed.
            case ChannelDisconnected closed:
                stats.RequestsLost += VoidUnprocessed(cn, tx, sessionId, closed.Timestamp);
                break;

            case UnrecognisedMove:
                stats.UnrecognisedMoves++;
                break;

            // A drag says how big both ends are. For a crate the game never names, that size
            // is the only thing there is to call it by, and hovering over it proves reach.
            case ContainerCapacitySeen cap:
                TouchContainer(cn, tx, cap.Source, cap.Timestamp, state.CurrentLocation, cap.SourceCapacity);
                TouchContainer(cn, tx, cap.Target, cap.Timestamp, state.CurrentLocation, cap.TargetCapacity);
                stats.CapacitiesSeen++;
                break;

            // No line carries both a location id and its name. The id arrives here...
            case LocationChanged change:
                state.PendingLocationIds.Clear();
                if (change.NewLocationId != "0") state.PendingLocationIds.Add(change.NewLocationId);
                if (change.NewLandingId != "0") state.PendingLocationIds.Add(change.NewLandingId);
                state.PendingSince = change.Timestamp;

                // Prefer the precise location over the landing zone it sits in.
                var here = change.NewLocationId != "0" ? change.NewLocationId
                         : change.NewLandingId != "0" ? change.NewLandingId
                         : null;
                if (here is not null)
                {
                    state.CurrentLocation = new PlayerLocation(here, change.Timestamp);
                    state.FirstLocation ??= state.CurrentLocation;
                    RecordArrival(cn, tx, here, change.Timestamp, state, stats);
                }
                break;

            // A quantum jump names where it is going, and the location the player enters right
            // on arrival is the place around that point. For a spot in open space — a mining
            // site the player parked at and logged out — this is the only name there ever is.
            case QuantumTargetSelected target:
                state.QuantumTarget = target.Point;
                state.QuantumArrivedAt = null;
                break;

            case QuantumArrived arrived:
                state.QuantumArrivedAt = arrived.Timestamp;
                break;

            // ...and the name in the next request, usually within a couple of seconds.
            case LocationNamed named:
            {
                if (state.PendingLocationIds.Count == 0) break;
                if (named.Timestamp - state.PendingSince > LocationPairingWindow)
                {
                    state.PendingLocationIds.Clear();
                    break;
                }

                foreach (var id in state.PendingLocationIds)
                {
                    if (UpsertLocationName(cn, tx, id, named.Name, named.Timestamp))
                    {
                        stats.LocationsNamed++;
                    }
                    else
                    {
                        stats.LocationConflicts++;
                    }
                }

                state.PendingLocationIds.Clear();
                break;
            }

            // The player-facing name of wherever the player is standing. A name that is
            // just a system means the route was plotted from open space, and it names the
            // region the player is flying through rather than the station they left — the
            // gateway to Nyx sits in Stanton, so adopting it as a system would be wrong.
            case PlaceNamed place:
            {
                if (state.CurrentLocation is not { } at) break;
                if (Naming.Systems.IsSystemOnly(place.DisplayName)) break;

                RecordPlaceEvidence(cn, tx, at.LocationId, "name", place.DisplayName, place.Timestamp);
                stats.PlaceEvidence++;
                break;
            }

            // A station's own object containers stream in as the player arrives. Later
            // paths are scenery passed on the way somewhere else, so only the arrival
            // window is trusted.
            case PlaceAssetSeen asset:
            {
                if (state.CurrentLocation is not { } where) break;
                if (asset.Timestamp - where.Since > AssetAttributionWindow) break;

                RecordPlaceEvidence(cn, tx, where.LocationId, "system", asset.System, asset.Timestamp);
                stats.PlaceEvidence++;
                break;
            }
        }
    }

    /// <summary>
    /// How soon after a quantum arrival the location change that follows it belongs to it. On
    /// the local corpus it comes within three seconds.
    /// </summary>
    private static readonly TimeSpan ArrivalPairingWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Mission beacons are one-off points with a fresh name every time, so they name nothing
    /// worth remembering.
    /// </summary>
    private const string MissionPointPrefix = "MISSION_";

    /// <summary>Records the quantum point the player just arrived at as evidence for <paramref name="here"/>.</summary>
    private static void RecordArrival(
        SqliteConnection cn, SqliteTransaction tx, string here, DateTimeOffset at, SessionState state, IngestStats stats)
    {
        if (state.QuantumArrivedAt is not { } arrived || state.QuantumTarget is not { } point) return;

        // Only the first location change after an arrival: the ones after it are the player
        // moving on.
        state.QuantumArrivedAt = null;
        if (at - arrived > ArrivalPairingWindow) return;
        if (point.StartsWith(MissionPointPrefix, StringComparison.OrdinalIgnoreCase)) return;

        RecordPlaceEvidence(cn, tx, here, "arrival", point, at);
        stats.PlaceEvidence++;
    }

    /// <summary>Inserts a move and registers it so a later completion line can resolve it.</summary>
    private static void Record(
        SqliteConnection cn,
        SqliteTransaction tx,
        long sessionId,
        long lineOffset,
        ItemMoved move,
        string? caller,
        SessionState state,
        IngestStats stats,
        int itemIx = 0)
    {
        // Both ends of a drag were in reach when it happened, which is what places a box
        // the game never identifies. Done before the dedupe below so a re-read of a live
        // file still pins containers whose moves were ingested on an earlier pass.
        TouchContainer(cn, tx, move.Source, move.Timestamp, state.CurrentLocation);
        TouchContainer(cn, tx, move.Target, move.Timestamp, state.CurrentLocation);

        // The spawn that names the instance sometimes lands before the move it fulfils.
        if (move.ItemGeid is null &&
            state.PendingGeids.TryGetValue(move.RequestNo, out var spawned) &&
            spawned.ClassName == move.ItemClass &&
            move.Timestamp - spawned.At <= GeidPairingWindow)
        {
            move = move with { ItemGeid = spawned.Geid };
            stats.GeidsRecovered++;
        }

        var id = InsertMove(cn, tx, sessionId, lineOffset, move, caller, itemIx);
        if (id is null) return; // already ingested on an earlier pass

        // The counter restarts on shard changes, so a repeated number always means the
        // older request has finished and its slot can be reused. A batch keeps every row
        // under one number so that one completion line resolves all of them.
        if (itemIx == 0) state.MoveByRequest.Remove(move.RequestNo);
        if (!state.MoveByRequest.TryGetValue(move.RequestNo, out var list))
        {
            list = [];
            state.MoveByRequest[move.RequestNo] = list;
        }
        list.Add(new PendingMove(id.Value, move.Timestamp));

        stats.MovesInserted++;
    }

    /// <summary>A move row still waiting for its completion line, and possibly its geid.</summary>
    private readonly record struct PendingMove(long Id, DateTimeOffset At);

    /// <summary>
    /// Per-file parse state. None of this is persisted: it only correlates lines that
    /// are close together within one log file.
    /// </summary>
    private sealed class SessionState
    {
        public Dictionary<int, string> PendingCallers { get; } = [];
        public Dictionary<int, List<PendingMove>> MoveByRequest { get; } = [];

        /// <summary>Entity spawns by request number, for moves that arrive after them.</summary>
        public Dictionary<int, (string Geid, string ClassName, DateTimeOffset At)> PendingGeids { get; } = [];

        public List<string> PendingLocationIds { get; } = [];
        public DateTimeOffset PendingSince { get; set; }

        /// <summary>Where the player is right now, used to pin containers they open.</summary>
        public PlayerLocation? CurrentLocation { get; set; }

        /// <summary>
        /// The first place this pass saw the player arrive. Only the store's first one is
        /// kept, so a pass that resumes mid-file cannot overwrite the spawn with a later stop.
        /// </summary>
        public PlayerLocation? FirstLocation { get; set; }

        /// <summary>The quantum destination last selected, and when the drive arrived at it.</summary>
        public string? QuantumTarget { get; set; }
        public DateTimeOffset? QuantumArrivedAt { get; set; }

        /// <summary>The newest attachment line: when, and the burst it opened or joined.</summary>
        public (DateTimeOffset At, long BurstId)? LastSighting { get; set; }

        /// <summary>The newest line of the current burst that a sub-attachment could hang off.</summary>
        public string? ParentCandidate { get; set; }
    }

    /// <summary>
    /// Attachment lines closer together than this are one burst. A whole enumeration of the
    /// body lands within two milliseconds; the next one is tens of seconds away at the least.
    /// </summary>
    private static readonly TimeSpan BurstGap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Stores one attachment line, with the burst it belongs to and, for a sub-attachment, the
    /// item it hangs off.
    /// </summary>
    private static void RecordSighting(
        SqliteConnection cn, SqliteTransaction tx, long sessionId, long lineOffset, AttachmentSeen worn,
        SessionState state)
    {
        // A line stamped earlier than the one before it is not part of that burst either.
        var burstId = state.LastSighting is { } last
                      && worn.Timestamp >= last.At && worn.Timestamp - last.At < BurstGap
            ? last.BurstId
            : lineOffset;
        if (burstId == lineOffset) state.ParentCandidate = null;
        state.LastSighting = (worn.Timestamp, burstId);

        string? parent = null;
        if (worn.IsPart) parent = state.ParentCandidate;
        else state.ParentCandidate = worn.Geid;

        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO attachment_sighting(
                session_id, line_offset, ts, geid, class_name, port, burst_id, parent_geid, placeholder)
            VALUES($s, $o, $t, $g, $c, $p, $b, $parent, $ph)
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$o", lineOffset);
        cmd.Parameters.AddWithValue("$t", Iso(worn.Timestamp));
        cmd.Parameters.AddWithValue("$g", worn.Geid);
        cmd.Parameters.AddWithValue("$c", worn.ClassName);
        cmd.Parameters.AddWithValue("$p", worn.Port);
        cmd.Parameters.AddWithValue("$b", burstId);
        cmd.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ph", worn.IsPlaceholder ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Picks the burst tracking up where the store left it, so a burst split across two
    /// watcher passes stays one burst and its children still find their parent.
    /// </summary>
    private static void PreloadLastBurst(SqliteConnection cn, long sessionId, SessionState state)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT ts, burst_id, geid, port FROM attachment_sighting
            WHERE session_id = $s
              AND burst_id = (SELECT burst_id FROM attachment_sighting
                              WHERE session_id = $s ORDER BY line_offset DESC LIMIT 1)
            ORDER BY line_offset
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            state.LastSighting = (DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.GetInt64(1));
            if (!AttachmentSeen.IsPartPort(r.GetString(3))) state.ParentCandidate = r.GetString(2);
        }
    }

    private sealed record SessionRow(
        long Id, long Offset, bool Complete, DateTimeOffset? FirstTs, PlayerLocation? CurrentLocation,
        int? Build = null);

    private static SessionRow LoadOrCreateSession(SqliteConnection cn, string path)
    {
        using (var sel = cn.CreateCommand())
        {
            sel.CommandText = """
                SELECT id, byte_offset, complete, first_ts, cur_loc_id, cur_loc_since, build
                FROM session WHERE path = $p
                """;
            sel.Parameters.AddWithValue("$p", path);
            using var r = sel.ExecuteReader();
            if (r.Read())
            {
                return new SessionRow(
                    r.GetInt64(0),
                    r.GetInt64(1),
                    r.GetInt32(2) != 0,
                    r.IsDBNull(3) ? null : ParseIso(r.GetString(3)),
                    r.IsDBNull(4) || r.IsDBNull(5)
                        ? null
                        : new PlayerLocation(r.GetString(4), ParseIso(r.GetString(5))),
                    r.IsDBNull(6) ? null : r.GetInt32(6));
            }
        }

        using var ins = cn.CreateCommand();
        ins.CommandText = "INSERT INTO session(path) VALUES($p); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$p", path);
        return new SessionRow((long)ins.ExecuteScalar()!, 0, false, null, null);
    }

    private static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// Starts a session over when the file behind its path is not the one it was built from.
    /// <para>
    /// Game.log keeps its name across a rotation, so the stored byte watermark is the only
    /// thing tying a session to a file — and a watermark alone cannot tell a longer file from
    /// a different one. Shrinkage is caught by <see cref="ByteLineReader"/>, but a rotation
    /// that happens while the tracker is closed is not: by the time it runs again the new
    /// Game.log may already be past the old offset, and ingestion would resume in the middle
    /// of an unrelated file. Every line before that point would be missed, and the recorded
    /// line offsets — which are a move's identity — would collide with the previous file's
    /// under the same session, so the new rows would be dropped as duplicates.
    /// </para>
    /// <para>
    /// The first line's timestamp identifies the file cheaply and survives the copy, restore
    /// and tunnelling cases that make file creation time unreliable.
    /// </para>
    /// </summary>
    private static SessionRow DiscardIfRotated(SqliteConnection cn, SessionRow session, LogFile file)
    {
        if (session.Offset == 0 || session.FirstTs is not { } knownFirst) return session;

        var actualFirst = LogFileLocator.FirstTimestamp(file.Path);
        if (actualFirst is null || actualFirst == knownFirst) return session;

        using var tx = cn.BeginTransaction();

        // The rotated content is not lost: it now sits in logbackups/ under its own name and
        // is ingested as a separate session. Keeping these rows would only collide with the
        // new file's offsets.
        using (var del = cn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = """
                DELETE FROM move WHERE session_id = $s;
                DELETE FROM attachment_sighting WHERE session_id = $s;
                """;
            del.Parameters.AddWithValue("$s", session.Id);
            del.ExecuteNonQuery();
        }

        using (var reset = cn.CreateCommand())
        {
            reset.Transaction = tx;
            reset.CommandText = """
                UPDATE session
                SET byte_offset = 0, first_ts = NULL, last_ts = NULL, complete = 0,
                    unrecognised = 0, last_line_ts = NULL, cur_loc_id = NULL, cur_loc_since = NULL,
                    build = NULL, first_loc_id = NULL, first_loc_since = NULL
                WHERE id = $s
                """;
            reset.Parameters.AddWithValue("$s", session.Id);
            reset.ExecuteNonQuery();
        }

        tx.Commit();

        return session with { Offset = 0, FirstTs = null, CurrentLocation = null, Build = null };
    }

    /// <summary>
    /// Resuming mid-file loses the in-memory request map, which would leave the
    /// completions that follow the watermark unmatched. Restore the unresolved ones, and the
    /// ones marked lost: a completion that still turns up must be able to overwrite the mark.
    /// </summary>
    private static void PreloadPendingRequests(SqliteConnection cn, long sessionId, SessionState state)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT request_no, id, ts FROM move
            WHERE session_id = $s AND (result IS NULL OR result = $lost)
            ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$lost", LostResult);
        cmd.Parameters.AddWithValue("$limit", PreloadLimit);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Descending, so the first rows for a number are the most recent ones.
            var pending = new PendingMove(
                r.GetInt64(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture));

            if (state.MoveByRequest.TryGetValue(r.GetInt32(0), out var list)) list.Add(pending);
            else state.MoveByRequest[r.GetInt32(0)] = [pending];
        }
    }

    private static void SaveSession(
        SqliteConnection cn,
        SqliteTransaction tx,
        long id,
        long offset,
        DateTimeOffset? firstTs,
        DateTimeOffset? lastTs,
        DateTimeOffset? newestLineTs,
        int unrecognisedDelta,
        PlayerLocation? currentLocation,
        PlayerLocation? firstLocation,
        int? build,
        bool complete)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE session SET
                byte_offset   = $o,
                complete      = $c,
                first_ts      = COALESCE(first_ts, $f),
                last_ts       = COALESCE($l, last_ts),
                last_line_ts  = COALESCE($nl, last_line_ts),
                unrecognised  = unrecognised + $u,
                cur_loc_id    = $loc,
                cur_loc_since = $locs,
                build         = COALESCE(build, $b),
                first_loc_since = CASE WHEN first_loc_id IS NULL THEN $fls ELSE first_loc_since END,
                first_loc_id    = COALESCE(first_loc_id, $fl)
            WHERE id = $i
            """;
        cmd.Parameters.AddWithValue("$o", offset);
        cmd.Parameters.AddWithValue("$c", complete ? 1 : 0);
        cmd.Parameters.AddWithValue("$f", Iso(firstTs));
        cmd.Parameters.AddWithValue("$l", Iso(lastTs));
        cmd.Parameters.AddWithValue("$nl", Iso(newestLineTs));
        cmd.Parameters.AddWithValue("$u", unrecognisedDelta);
        cmd.Parameters.AddWithValue("$loc", (object?)currentLocation?.LocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$locs", Iso(currentLocation?.Since));
        cmd.Parameters.AddWithValue("$b", (object?)build ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fl", (object?)firstLocation?.LocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fls", Iso(firstLocation?.Since));
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    private static long? InsertMove(
        SqliteConnection cn,
        SqliteTransaction tx,
        long sessionId,
        long lineOffset,
        ItemMoved move,
        string? caller,
        int itemIx)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO move(
                session_id, line_offset, item_ix, request_no, ts, player, player_id, move_type,
                item_class, item_geid, amount,
                src_raw, src_kind, src_key, tgt_raw, tgt_kind, tgt_key, caller, action)
            VALUES($s, $o, $ix, $r, $ts, $p, $pid, $mt, $ic, $ig, $amt,
                   $sr, $sk, $skey, $tr, $tk, $tkey, $c, $act);
            SELECT CASE WHEN changes() = 0 THEN NULL ELSE last_insert_rowid() END;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$o", lineOffset);
        cmd.Parameters.AddWithValue("$ix", itemIx);
        cmd.Parameters.AddWithValue("$r", move.RequestNo);
        cmd.Parameters.AddWithValue("$ts", Iso(move.Timestamp));
        cmd.Parameters.AddWithValue("$p", move.Player);
        cmd.Parameters.AddWithValue("$pid", move.PlayerId);
        cmd.Parameters.AddWithValue("$mt", move.MoveType);
        cmd.Parameters.AddWithValue("$ic", move.ItemClass);
        cmd.Parameters.AddWithValue("$ig", (object?)move.ItemGeid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$amt", move.Amount);
        cmd.Parameters.AddWithValue("$sr", move.Source.Raw);
        cmd.Parameters.AddWithValue("$sk", move.Source.Kind.ToString());
        cmd.Parameters.AddWithValue("$skey", move.Source.HoldingKey);
        cmd.Parameters.AddWithValue("$tr", move.Target.Raw);
        cmd.Parameters.AddWithValue("$tk", move.Target.Kind.ToString());
        cmd.Parameters.AddWithValue("$tkey", move.Target.HoldingKey);
        cmd.Parameters.AddWithValue("$c", (object?)caller ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$act", (object?)move.Action ?? DBNull.Value);

        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    /// <summary>
    /// Names the instance behind a class-level move. The class must agree: request numbers
    /// are reused, so a matching number alone is not enough to bind an entity to a row.
    /// </summary>
    private static bool ApplyGeid(
        SqliteConnection cn, SqliteTransaction tx, long moveId, string geid, string className)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE move SET item_geid = $g
            WHERE id = $i AND item_geid IS NULL AND item_class = $c
            """;
        cmd.Parameters.AddWithValue("$g", geid);
        cmd.Parameters.AddWithValue("$i", moveId);
        cmd.Parameters.AddWithValue("$c", className);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Keeps only the newest sighting of a worn port — the line repeats on every spawn.</summary>
    private static void UpsertAttachment(SqliteConnection cn, SqliteTransaction tx, AttachmentSeen worn)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attachment(geid, class_name, port, last_seen)
            VALUES($g, $c, $p, $t)
            ON CONFLICT(geid) DO UPDATE SET
                class_name = $c,
                port       = $p,
                last_seen  = MAX(last_seen, $t)
            """;
        cmd.Parameters.AddWithValue("$g", worn.Geid);
        cmd.Parameters.AddWithValue("$c", worn.ClassName);
        cmd.Parameters.AddWithValue("$p", worn.Port);
        cmd.Parameters.AddWithValue("$t", Iso(worn.Timestamp));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Adds one vote for what a location id is called, or which system it sits in.</summary>
    private static void RecordPlaceEvidence(
        SqliteConnection cn, SqliteTransaction tx,
        string locationId, string kind, string value, DateTimeOffset ts)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO place_evidence(location_id, kind, value, hits, last_seen)
            VALUES($i, $k, $v, 1, $t)
            ON CONFLICT(location_id, kind, value) DO UPDATE SET
                hits      = hits + 1,
                last_seen = MAX(last_seen, $t)
            """;
        cmd.Parameters.AddWithValue("$i", locationId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$t", Iso(ts));
        cmd.ExecuteNonQuery();
    }

    /// <summary>What a request the server never processed is recorded as.</summary>
    public const string LostResult = "lost";

    /// <summary>
    /// Marks every request of the session still without a result as lost, up to
    /// <paramref name="before"/> when given.
    /// <para>
    /// Across every log since 2026-08-04, exactly four request-backed moves never got a
    /// result: the four the server dropped at 15:53 on 2026-09-25. So nothing that was
    /// processed is caught by this. The rows from <c>OnInventoryStoreItem</c> are left alone:
    /// they are companions of a Store the request line records, never requests themselves, and
    /// never get a result. A completion that still turns up later overwrites the mark.
    /// </para>
    /// </summary>
    private static int VoidUnprocessed(SqliteConnection cn, SqliteTransaction tx, long sessionId, DateTimeOffset? before)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE move SET result = $lost
            WHERE session_id = $s AND result IS NULL AND request_no > 0 AND move_type <> 'StoreItem'
              AND ($before IS NULL OR ts <= $before)
            """;
        cmd.Parameters.AddWithValue("$lost", LostResult);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$before", Iso(before));
        return cmd.ExecuteNonQuery();
    }

    private static void UpdateResult(SqliteConnection cn, SqliteTransaction tx, long moveId, string result)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE move SET result = $r WHERE id = $i";
        cmd.Parameters.AddWithValue("$r", result);
        cmd.Parameters.AddWithValue("$i", moveId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records a container's class and, when known, where the player stood when they
    /// opened it. Only ever moves the location forward in time so that replaying an
    /// older log file cannot overwrite a newer sighting.
    /// </summary>
    private static void UpsertContainer(
        SqliteConnection cn, SqliteTransaction tx, ContainerIdentified c, PlayerLocation? at) =>
        UpsertContainer(cn, tx, c.Geid, c.ClassName, capacity: null, c.Timestamp, at);

    /// <summary>
    /// Notes that a container was within the player's reach, whatever it is, and how much it
    /// holds when the line says so. Dragging an item into or out of one proves reach as surely
    /// as opening it does, and it is the only evidence there is for a box the game never
    /// names: a container that is only ever a move endpoint gets no Token Flow line, so
    /// without this it has no row at all and everything stored in it resolves to nowhere.
    /// </summary>
    private static void TouchContainer(
        SqliteConnection cn, SqliteTransaction tx, InventoryRef inv, DateTimeOffset at,
        PlayerLocation? here, long? capacity = null)
    {
        if (inv.Kind != InventoryKind.Container || inv.HoldingKey.Length == 0) return;

        // -1 is what an INVALID side reports, and 0 says nothing either.
        UpsertContainer(
            cn, tx, inv.HoldingKey, className: null, capacity > 0 ? capacity : null, at, here);
    }

    private static void UpsertContainer(
        SqliteConnection cn, SqliteTransaction tx,
        string geid, string? className, long? capacity, DateTimeOffset seenAt, PlayerLocation? at)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;

        // COALESCE keeps what an earlier line already established: a sighting that only
        // proves reach must never erase the class, nor the capacity, that another line gave.
        cmd.CommandText = """
            INSERT INTO container_class(geid, class_name, last_seen, last_loc_id, last_loc_ts, capacity)
            VALUES($g, $c, $t, $loc, $lts, $cap)
            ON CONFLICT(geid) DO UPDATE SET
                class_name  = COALESCE($c, class_name),
                capacity    = COALESCE($cap, capacity),
                last_seen   = MAX(last_seen, $t),
                last_loc_id = CASE WHEN $loc IS NOT NULL AND ($lts > last_loc_ts OR last_loc_ts IS NULL)
                                   THEN $loc ELSE last_loc_id END,
                last_loc_ts = CASE WHEN $loc IS NOT NULL AND ($lts > last_loc_ts OR last_loc_ts IS NULL)
                                   THEN $lts ELSE last_loc_ts END
            """;
        cmd.Parameters.AddWithValue("$g", geid);
        cmd.Parameters.AddWithValue("$c", (object?)className ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cap", (object?)capacity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", Iso(seenAt));
        cmd.Parameters.AddWithValue("$loc", (object?)at?.LocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lts", at is null ? DBNull.Value : Iso(seenAt));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Binds a location id to a name. Returns false if the id is already bound to a
    /// different name — a signal the pairing heuristic has broken and diagnostics
    /// should surface it rather than silently overwriting.
    /// </summary>
    private static bool UpsertLocationName(
        SqliteConnection cn, SqliteTransaction tx, string id, string name, DateTimeOffset ts)
    {
        using var sel = cn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "SELECT name FROM location_name WHERE location_id = $i";
        sel.Parameters.AddWithValue("$i", id);
        var existing = sel.ExecuteScalar() as string;

        if (existing is not null && existing != name) return false;

        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO location_name(location_id, name, first_seen, evidence_count)
            VALUES($i, $n, $t, 1)
            ON CONFLICT(location_id) DO UPDATE SET evidence_count = evidence_count + 1
            """;
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", Iso(ts));
        cmd.ExecuteNonQuery();
        return true;
    }

    private static object Iso(DateTimeOffset? ts) =>
        ts is null ? DBNull.Value : ts.Value.UtcDateTime.ToString("O");
}
