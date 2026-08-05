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
    public int PlaceEvidence { get; set; }
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

    public string LogDir { get; } = logDir;

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
        if (session.Complete) return;

        var reader = new ByteLineReader(session.Offset);
        var state = new SessionState();
        PreloadPendingRequests(cn, session.Id, state);

        using var tx = cn.BeginTransaction();

        var parsedAny = false;
        DateTimeOffset? firstTs = null, lastTs = null;

        foreach (var line in reader.ReadLines(file.Path))
        {
            stats.LinesRead++;

            if (!LogLine.TryParse(line.Text, out var log)) continue;
            if (log.Timestamp < _sinceTs) continue;

            firstTs ??= log.Timestamp;
            lastTs = log.Timestamp;
            parsedAny = true;

            var ev = InventoryEventParser.Parse(log);
            if (ev is null) continue;

            Apply(cn, tx, session.Id, line.StartOffset, ev, state, stats);
        }

        SaveSession(cn, tx, session.Id, reader.Offset, firstTs, lastTs, complete: !file.IsLive);
        tx.Commit();

        if (parsedAny) stats.FilesParsed++;
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
                UpsertAttachment(cn, tx, worn);
                stats.AttachmentsSeen++;
                break;

            // The entry is deliberately left in place: the entity spawn that names the
            // instance arrives *after* the completion, and dropping the mapping here would
            // lose the only chance to bind them. The next move to reuse the number clears it.
            case MoveCompleted done:
                if (state.MoveByRequest.TryGetValue(done.RequestNo, out var moves))
                {
                    foreach (var m in moves) UpdateResult(cn, tx, m.Id, done.Result);
                    stats.CompletionsApplied++;
                }
                break;

            case ContainerIdentified container:
                UpsertContainer(cn, tx, container, state.CurrentLocation);
                stats.ContainersIdentified++;
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
                if (here is not null) state.CurrentLocation = new PlayerLocation(here, change.Timestamp);
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
    }

    private sealed record SessionRow(long Id, long Offset, bool Complete);

    private static SessionRow LoadOrCreateSession(SqliteConnection cn, string path)
    {
        using (var sel = cn.CreateCommand())
        {
            sel.CommandText = "SELECT id, byte_offset, complete FROM session WHERE path = $p";
            sel.Parameters.AddWithValue("$p", path);
            using var r = sel.ExecuteReader();
            if (r.Read()) return new SessionRow(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2) != 0);
        }

        using var ins = cn.CreateCommand();
        ins.CommandText = "INSERT INTO session(path) VALUES($p); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$p", path);
        return new SessionRow((long)ins.ExecuteScalar()!, 0, false);
    }

    /// <summary>
    /// Resuming mid-file loses the in-memory request map, which would leave the
    /// completions that follow the watermark unmatched. Restore the unresolved ones.
    /// </summary>
    private static void PreloadPendingRequests(SqliteConnection cn, long sessionId, SessionState state)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT request_no, id, ts FROM move
            WHERE session_id = $s AND result IS NULL
            ORDER BY id DESC LIMIT 500
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);

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
        bool complete)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE session SET
                byte_offset = $o,
                complete    = $c,
                first_ts    = COALESCE(first_ts, $f),
                last_ts     = COALESCE($l, last_ts)
            WHERE id = $i
            """;
        cmd.Parameters.AddWithValue("$o", offset);
        cmd.Parameters.AddWithValue("$c", complete ? 1 : 0);
        cmd.Parameters.AddWithValue("$f", Iso(firstTs));
        cmd.Parameters.AddWithValue("$l", Iso(lastTs));
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
                src_raw, src_kind, src_key, tgt_raw, tgt_kind, tgt_key, caller)
            VALUES($s, $o, $ix, $r, $ts, $p, $pid, $mt, $ic, $ig, $amt,
                   $sr, $sk, $skey, $tr, $tk, $tkey, $c);
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
