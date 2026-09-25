using InventoryTracker.Ingest;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Data;

/// <summary>
/// Owns the SQLite store and its schema. Connections are cheap and short-lived;
/// callers open one per unit of work via <see cref="Open"/>.
/// </summary>
public sealed class TrackerDb
{
    public string Path { get; }
    private readonly string _connectionString;

    public TrackerDb(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Private cache, not shared: shared-cache mode moves contention to table-level
        // locks within the process and surfaces as SQLITE_LOCKED, which the busy handler
        // cannot retry. WAL already gives concurrent readers alongside the single writer,
        // which is exactly the shape here (background ingest writes, UI reads).
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>The default store location, alongside the user's other app data.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InventoryTracker",
        "tracker.db");

    public SqliteConnection Open()
    {
        var cn = new SqliteConnection(_connectionString);
        cn.Open();

        // busy_timeout makes a writer wait for a competing one rather than failing
        // immediately; the UI's "rebuild now" and the background sweep can overlap.
        Execute(cn, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=30000;
            """);
        return cn;
    }

    /// <summary>
    /// Bump whenever <see cref="Schema"/> changes shape. The store is a derived cache —
    /// logs are the source of truth — so a mismatch is resolved by rebuilding rather
    /// than by writing migrations.
    /// <para>
    /// 8 -> 9 is required for more than the new <c>session.unrecognised</c> /
    /// <c>session.last_line_ts</c> columns: it also forces every existing store to rebuild
    /// from scratch. Before this fix, the parser silently read zero moves from any build
    /// 12519617+ log — but it still advanced each session's byte watermark past those lines
    /// and, for a rotated file, marked it <c>complete</c>. Without the bump, the fixed parser
    /// would never be given those bytes to re-read: it would resume exactly where the broken
    /// one left off, or skip the file entirely.
    /// </para>
    /// <para>9 -> 10 adds <c>session.cur_loc_id</c> / <c>cur_loc_since</c>.</para>
    /// </summary>
    private const int SchemaVersion = 10;

    public void Initialize()
    {
        using var cn = Open();

        // A store built before versioning existed reports null, which is still a mismatch.
        if (HasTable(cn, "move") && ReadMeta(cn, "schema_version") != SchemaVersion)
        {
            Execute(cn, DropAll);
        }

        Execute(cn, Schema);
        WriteMeta(cn, "schema_version", SchemaVersion);

        // Same tables, different extraction: what an older parser left behind is missing
        // whatever the newer one learned to read, and completed files are never reopened
        // on their own. Schema 9 had to be bumped just to force that; this does it for any
        // parser change without touching the schema.
        if (ReadMeta(cn, "parser_version") != InventoryEventParser.Version)
        {
            Execute(cn, ClearIngest);
            WriteMeta(cn, "parser_version", InventoryEventParser.Version);
        }
    }

    private static void WriteMeta(SqliteConnection cn, string key, int value)
    {
        using var stamp = cn.CreateCommand();
        stamp.CommandText =
            "INSERT INTO meta(key, value) VALUES($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = $v";
        stamp.Parameters.AddWithValue("$k", key);
        stamp.Parameters.AddWithValue("$v", value.ToString());
        stamp.ExecuteNonQuery();
    }

    private static bool HasTable(SqliteConnection cn, string name)
    {
        using var probe = cn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n";
        probe.Parameters.AddWithValue("$n", name);
        return probe.ExecuteScalar() is not null;
    }

    private static int? ReadMeta(SqliteConnection cn, string key)
    {
        if (!HasTable(cn, "meta")) return null;

        using var probe = cn.CreateCommand();
        probe.CommandText = "SELECT value FROM meta WHERE key = $k";
        probe.Parameters.AddWithValue("$k", key);
        return probe.ExecuteScalar() is string s && int.TryParse(s, out var v) ? v : null;
    }

    /// <summary>Drops everything derived from logs. Wiki name cache is kept — it is expensive to refetch.</summary>
    public void ResetIngest()
    {
        using var cn = Open();
        Execute(cn, ClearIngest);
    }

    private const string ClearIngest = """
        DELETE FROM move;
        DELETE FROM session;
        DELETE FROM location_name;
        DELETE FROM container_class;
        DELETE FROM attachment;
        DELETE FROM place_evidence;
        """;

    private static void Execute(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS session (
            id           INTEGER PRIMARY KEY,
            path         TEXT    NOT NULL UNIQUE,
            first_ts     TEXT,
            last_ts      TEXT,
            byte_offset  INTEGER NOT NULL DEFAULT 0,
            complete     INTEGER NOT NULL DEFAULT 0,
            -- Running count of UnrecognisedMove lines seen across every ingest pass over
            -- this file. Added to, never overwritten, so it keeps growing as a live file
            -- does instead of resetting on each pass.
            unrecognised INTEGER NOT NULL DEFAULT 0,
            -- Timestamp of the newest well-formed log line in the file, independent of the
            -- inception-date filter. Unlike last_line_ts, first_ts is also captured before
            -- that filter (so DiscardIfRotated can identify a file regardless of where the
            -- cutoff falls) and is therefore set for any file with any line at all; last_ts
            -- is the one still gated by the filter. This is what lets diagnostics say "your
            -- newest log is from before your inception date" instead of a file with real
            -- content looking empty.
            last_line_ts TEXT,
            -- Where the player stood at byte_offset. The game writes it once, on arrival,
            -- so a pass that resumes mid-file has no other way to know it — and without
            -- it every container opened afterwards goes unplaced.
            cur_loc_id    TEXT,
            cur_loc_since TEXT
        );

        -- One row per item relocation.
        -- request_no is NOT a stable key: it restarts at 1 in every log file *and*
        -- resets again mid-file whenever the client changes shard (observed three
        -- times in one 16 Jul log). Identity is therefore the line's byte offset,
        -- which is unique and stable across re-ingest.
        CREATE TABLE IF NOT EXISTS move (
            id          INTEGER PRIMARY KEY,
            session_id  INTEGER NOT NULL REFERENCES session(id) ON DELETE CASCADE,
            line_offset INTEGER NOT NULL,
            request_no  INTEGER NOT NULL,
            ts          TEXT    NOT NULL,
            player      TEXT,
            player_id   TEXT,
            move_type   TEXT    NOT NULL,
            item_class  TEXT    NOT NULL,
            -- NULL for every type except Store: the log names an exact entity only there.
            -- Class-level moves say "one of these moved", not which one.
            item_geid   TEXT,
            amount      INTEGER NOT NULL DEFAULT 1,
            -- Position within a multi-select drag, whose several classes all share one
            -- log line and therefore one byte offset. 0 for an ordinary single move.
            item_ix     INTEGER NOT NULL DEFAULT 0,
            src_raw     TEXT,
            src_kind    TEXT,
            src_key     TEXT,
            tgt_raw     TEXT,
            tgt_kind    TEXT,
            tgt_key     TEXT,
            caller      TEXT,
            result      TEXT,
            UNIQUE(session_id, line_offset, item_ix)
        );

        CREATE INDEX IF NOT EXISTS ix_move_geid  ON move(item_geid, ts);
        CREATE INDEX IF NOT EXISTS ix_move_class ON move(item_class, ts);
        CREATE INDEX IF NOT EXISTS ix_move_tgt   ON move(tgt_kind, tgt_key);

        CREATE TABLE IF NOT EXISTS location_name (
            location_id    TEXT PRIMARY KEY,
            name           TEXT NOT NULL,
            first_seen     TEXT,
            evidence_count INTEGER NOT NULL DEFAULT 0
        );

        -- Opening a container proves it was within reach, so the player's location at
        -- that moment places it. For SCU boxes and lootable crates -- which are set down
        -- and never "stored" anywhere -- this is the only thing that locates them at all.
        -- The class is nullable because a container the player only ever moves items in and
        -- out of is never named by any log line: the row then exists purely to pin it.
        -- capacity is in µSCU, from the drag lines: 2,000,000 is a 2 SCU crate. It is the
        -- only description of a container the game never names.
        CREATE TABLE IF NOT EXISTS container_class (
            geid           TEXT PRIMARY KEY,
            class_name     TEXT,
            last_seen      TEXT,
            last_loc_id    TEXT,
            last_loc_ts    TEXT,
            capacity       INTEGER
        );

        -- Ports worn on the player's body, from <AttachmentReceived>. Re-emitted on every
        -- spawn, so only the newest sighting per entity is kept. This is the only
        -- authoritative enumeration the game ever logs, and the only positive proof that
        -- an item is on the player rather than sitting at a station.
        CREATE TABLE IF NOT EXISTS attachment (
            geid       TEXT PRIMARY KEY,
            class_name TEXT NOT NULL,
            port       TEXT,
            last_seen  TEXT NOT NULL
        );

        -- Corroboration for what a location id actually is. The internal string the game
        -- pairs with the id ("RR_JP_NyxCastra") is a legacy asset name that matches neither
        -- the place's real name ("Stanton Gateway") nor its system (Nyx), so both are voted
        -- on from sightings gathered while the player was standing there.
        --   kind = 'name'   -> <Calculate Route> "Projected Start Location is X"
        --   kind = 'system' -> the system segment of a streamed object-container path
        CREATE TABLE IF NOT EXISTS place_evidence (
            location_id TEXT    NOT NULL,
            kind        TEXT    NOT NULL,
            value       TEXT    NOT NULL,
            hits        INTEGER NOT NULL DEFAULT 0,
            last_seen   TEXT,
            PRIMARY KEY (location_id, kind, value)
        );

        CREATE TABLE IF NOT EXISTS wiki_item (
            class_name   TEXT PRIMARY KEY,
            uuid         TEXT,
            display_name TEXT,
            manufacturer TEXT,
            type_label   TEXT,
            image_url    TEXT,
            web_url      TEXT,
            fetched_at   TEXT
        );

        -- Dropped in schema 8. It was rebuilt from wiki_item on every catalogue refresh
        -- (~12k rows) and never read by anything: the name search it was built for was
        -- never written. Reinstate it alongside the feature, not before.
        DROP TABLE IF EXISTS wiki_item_fts;

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        """;

    /// <summary>
    /// Everything derived from the logs, dropped and rebuilt on a schema mismatch. The wiki
    /// catalogue is deliberately not in here: it costs ~62 network requests to refill and is
    /// not derived from anything a schema change would invalidate.
    /// </summary>
    private const string DropAll = """
        DROP TABLE IF EXISTS move;
        DROP TABLE IF EXISTS session;
        DROP TABLE IF EXISTS location_name;
        DROP TABLE IF EXISTS container_class;
        DROP TABLE IF EXISTS attachment;
        DROP TABLE IF EXISTS place_evidence;
        """;
}
