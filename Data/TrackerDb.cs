using Microsoft.Data.Sqlite;

namespace LogParser.Data;

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

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>The default store location, alongside the user's other app data.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SCLogParser",
        "tracker.db");

    public SqliteConnection Open()
    {
        var cn = new SqliteConnection(_connectionString);
        cn.Open();
        Execute(cn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        return cn;
    }

    /// <summary>
    /// Bump whenever <see cref="Schema"/> changes shape. The store is a derived cache —
    /// logs are the source of truth — so a mismatch is resolved by rebuilding rather
    /// than by writing migrations.
    /// </summary>
    private const int SchemaVersion = 4;

    public void Initialize()
    {
        using var cn = Open();

        // A store built before versioning existed reports null, which is still a mismatch.
        if (HasTable(cn, "move") && ReadSchemaVersion(cn) != SchemaVersion)
        {
            Execute(cn, DropAll);
        }

        Execute(cn, Schema);

        using var stamp = cn.CreateCommand();
        stamp.CommandText =
            "INSERT INTO meta(key, value) VALUES('schema_version', $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = $v";
        stamp.Parameters.AddWithValue("$v", SchemaVersion.ToString());
        stamp.ExecuteNonQuery();
    }

    private static bool HasTable(SqliteConnection cn, string name)
    {
        using var probe = cn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n";
        probe.Parameters.AddWithValue("$n", name);
        return probe.ExecuteScalar() is not null;
    }

    private static int? ReadSchemaVersion(SqliteConnection cn)
    {
        if (!HasTable(cn, "meta")) return null;

        using var probe = cn.CreateCommand();
        probe.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        return probe.ExecuteScalar() is string s && int.TryParse(s, out var v) ? v : null;
    }

    /// <summary>Drops everything derived from logs. Wiki name cache is kept — it is expensive to refetch.</summary>
    public void ResetIngest()
    {
        using var cn = Open();
        Execute(cn, """
            DELETE FROM move;
            DELETE FROM session;
            DELETE FROM location_name;
            DELETE FROM container_class;
            """);
    }

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
            complete     INTEGER NOT NULL DEFAULT 0
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
            src_raw     TEXT,
            src_kind    TEXT,
            src_key     TEXT,
            tgt_raw     TEXT,
            tgt_kind    TEXT,
            tgt_key     TEXT,
            caller      TEXT,
            result      TEXT,
            UNIQUE(session_id, line_offset)
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
        CREATE TABLE IF NOT EXISTS container_class (
            geid           TEXT PRIMARY KEY,
            class_name     TEXT NOT NULL,
            last_seen      TEXT,
            last_loc_id    TEXT,
            last_loc_ts    TEXT
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

        -- Search index over the catalogue: the user types a human name, we need class names.
        CREATE VIRTUAL TABLE IF NOT EXISTS wiki_item_fts USING fts5(
            class_name, display_name, manufacturer, type_label, tokenize = 'unicode61');

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        """;

    private const string DropAll = """
        DROP TABLE IF EXISTS move;
        DROP TABLE IF EXISTS session;
        DROP TABLE IF EXISTS location_name;
        DROP TABLE IF EXISTS container_class;
        DROP TABLE IF EXISTS wiki_item;
        DROP TABLE IF EXISTS wiki_item_fts;
        DROP TABLE IF EXISTS meta;
        """;
}
