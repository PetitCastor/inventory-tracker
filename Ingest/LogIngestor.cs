using LogParser.Data;
using LogParser.Model;
using Microsoft.Data.Sqlite;

namespace LogParser.Ingest;

public sealed record IngestStats
{
    public int FilesSeen { get; set; }
    public int FilesParsed { get; set; }
    public int LinesRead { get; set; }
    public int MovesInserted { get; set; }
    public int CompletionsApplied { get; set; }
    public int ContainersIdentified { get; set; }
    public int LocationsNamed { get; set; }
    public int LocationConflicts { get; set; }
}

/// <summary>
/// Reads Game.log files into the SQLite store. Ingestion is resumable (per-file byte
/// watermark) and idempotent (moves keyed by their line offset), so it can be run
/// repeatedly and continuously against a growing live log.
/// </summary>
public sealed class LogIngestor(TrackerDb db, string logDir)
{
    /// <summary>
    /// A location id is bound to the name in the next RequestLocationInventory. Beyond
    /// this gap the two lines are unrelated and pairing them would invent a mapping.
    /// </summary>
    private static readonly TimeSpan LocationPairingWindow = TimeSpan.FromSeconds(30);

    public string LogDir { get; } = logDir;

    /// <summary>Parses everything not yet ingested. Safe to call on a timer.</summary>
    public IngestStats IngestAll(CancellationToken ct = default)
    {
        var stats = new IngestStats();
        var files = LogFileLocator.Discover(LogDir, InventoryEventParser.Cutoff);
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
            if (log.Timestamp < InventoryEventParser.Cutoff) continue;

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
                }
                break;

            case ItemMoved move:
            {
                var caller = state.PendingCallers.GetValueOrDefault(move.RequestNo);
                state.PendingCallers.Remove(move.RequestNo);

                var normalized = InventoryEventParser.Normalize(move, caller);
                if (normalized is null) break; // no real destination — tells us nothing

                Record(cn, tx, sessionId, lineOffset, normalized, caller, state, stats);
                break;
            }

            case MoveCompleted done:
                if (state.MoveByRequest.TryGetValue(done.RequestNo, out var moveId))
                {
                    UpdateResult(cn, tx, moveId, done.Result);
                    state.MoveByRequest.Remove(done.RequestNo);
                    stats.CompletionsApplied++;
                }
                break;

            case ContainerIdentified container:
                UpsertContainer(cn, tx, container, state.CurrentLocation);
                stats.ContainersIdentified++;
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
        IngestStats stats)
    {
        var id = InsertMove(cn, tx, sessionId, lineOffset, move, caller);
        if (id is null) return; // already ingested on an earlier pass

        // The counter restarts on shard changes, so a repeated number always means the
        // older request has finished and its slot can be reused.
        state.MoveByRequest[move.RequestNo] = id.Value;
        stats.MovesInserted++;
    }

    /// <summary>
    /// Per-file parse state. None of this is persisted: it only correlates lines that
    /// are close together within one log file.
    /// </summary>
    private sealed class SessionState
    {
        public Dictionary<int, string> PendingCallers { get; } = [];
        public Dictionary<int, long> MoveByRequest { get; } = [];
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
            SELECT request_no, id FROM move
            WHERE session_id = $s AND result IS NULL
            ORDER BY id DESC LIMIT 500
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Descending, so the first row for a number is the most recent one.
            state.MoveByRequest.TryAdd(r.GetInt32(0), r.GetInt64(1));
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
        string? caller)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO move(
                session_id, line_offset, request_no, ts, player, player_id, move_type,
                item_class, item_geid, amount,
                src_raw, src_kind, src_key, tgt_raw, tgt_kind, tgt_key, caller)
            VALUES($s, $o, $r, $ts, $p, $pid, $mt, $ic, $ig, $amt,
                   $sr, $sk, $skey, $tr, $tk, $tkey, $c);
            SELECT CASE WHEN changes() = 0 THEN NULL ELSE last_insert_rowid() END;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$o", lineOffset);
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
        SqliteConnection cn, SqliteTransaction tx, ContainerIdentified c, PlayerLocation? at)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO container_class(geid, class_name, last_seen, last_loc_id, last_loc_ts)
            VALUES($g, $c, $t, $loc, $lts)
            ON CONFLICT(geid) DO UPDATE SET
                class_name  = $c,
                last_seen   = MAX(last_seen, $t),
                last_loc_id = CASE WHEN $loc IS NOT NULL AND ($lts > last_loc_ts OR last_loc_ts IS NULL)
                                   THEN $loc ELSE last_loc_id END,
                last_loc_ts = CASE WHEN $loc IS NOT NULL AND ($lts > last_loc_ts OR last_loc_ts IS NULL)
                                   THEN $lts ELSE last_loc_ts END
            """;
        cmd.Parameters.AddWithValue("$g", c.Geid);
        cmd.Parameters.AddWithValue("$c", c.ClassName);
        cmd.Parameters.AddWithValue("$t", Iso(c.Timestamp));
        cmd.Parameters.AddWithValue("$loc", (object?)at?.LocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lts", at is null ? DBNull.Value : Iso(c.Timestamp));
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
