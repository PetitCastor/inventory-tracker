using InventoryTracker.Data;
using InventoryTracker.App;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

[CollectionDefinition("TrackerDbTests environment", DisableParallelization = true)]
public sealed class TrackerDbTestsEnvironmentCollection { }

[Collection("TrackerDbTests environment")]
public sealed class TrackerDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-db-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public TrackerDbTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        _db.Initialize();
        SeedDerivedData();
    }

    public void Dispose()
    {
        using (var cn = _db.Open())
        {
            cn.Close();
            SqliteConnection.ClearPool(cn);
        }
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ResolveDefaultPath_uses_configured_data_directory()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "itrk-data");

        var path = TrackerDb.ResolveDefaultPath(dataDirectory, "C:\\Users\\test\\AppData\\Local");

        Assert.Equal(Path.Combine(dataDirectory, "tracker.db"), path);
    }

    [Fact]
    public void ResolveDefaultPath_empty_data_directory_preserves_local_app_data_location()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "itrk-local");

        var path = TrackerDb.ResolveDefaultPath(string.Empty, localAppData);

        Assert.Equal(Path.Combine(localAppData, "InventoryTracker", "tracker.db"), path);
    }

    [Fact]
    public void ResolveDefaultPath_missing_data_directory_preserves_local_app_data_location()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "itrk-local");

        var path = TrackerDb.ResolveDefaultPath(null, localAppData);

        Assert.Equal(Path.Combine(localAppData, "InventoryTracker", "tracker.db"), path);
    }

    [Fact]
    public void Default_paths_use_data_directory_environment_variable()
    {
        var original = Environment.GetEnvironmentVariable("INVENTORYTRACKER_DATA_DIR");
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"itrk-env-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("INVENTORYTRACKER_DATA_DIR", dataDirectory);

            Assert.Equal(Path.Combine(dataDirectory, "tracker.db"), TrackerDb.DefaultPath);
            Assert.Equal(Path.Combine(dataDirectory, "config.json"), AppConfig.DefaultPath);
            Assert.Equal(Path.Combine(dataDirectory, "tracker.log"), FileLoggerProvider.DefaultPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INVENTORYTRACKER_DATA_DIR", original);
        }
    }

    [Fact]
    public void Default_paths_use_local_app_data_when_data_directory_environment_variable_is_cleared()
    {
        var original = Environment.GetEnvironmentVariable("INVENTORYTRACKER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("INVENTORYTRACKER_DATA_DIR", null);
            var localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InventoryTracker");

            Assert.Equal(Path.Combine(localAppData, "tracker.db"), TrackerDb.DefaultPath);
            Assert.Equal(Path.Combine(localAppData, "config.json"), AppConfig.DefaultPath);
            Assert.Equal(Path.Combine(localAppData, "tracker.log"), FileLoggerProvider.DefaultPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INVENTORYTRACKER_DATA_DIR", original);
        }
    }

    [Fact]
    public void ResetIngest_rolls_back_all_deletes_when_session_delete_fails()
    {
        var before = SnapshotTables(DerivedTables);
        using (var cn = _db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER block_session_delete BEFORE DELETE ON session BEGIN SELECT RAISE(ABORT, 'injected'); END;";
            cmd.ExecuteNonQuery();
        }

        var error = Assert.Throws<SqliteException>(() => _db.ResetIngest());

        Assert.Contains("injected", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, SnapshotTables(DerivedTables));
        Assert.Equal("1|1|42", Scalar("SELECT (SELECT COUNT(*) FROM move) || '|' || (SELECT COUNT(*) FROM attachment_sighting) || '|' || (SELECT byte_offset FROM session WHERE path = 'seed.log')"));
        Assert.Equal("1|42|{\"version\":1}", Scalar("SELECT (SELECT COUNT(*) FROM ingest_correlation) || '|' || (SELECT byte_offset FROM ingest_correlation WHERE session_id = 1) || '|' || (SELECT state_json FROM ingest_correlation WHERE session_id = 1)"));
    }

    [Fact]
    public void ResetIngest_clears_derived_tables_and_keeps_confirmations_and_catalogues()
    {
        _db.ResetIngest();

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM attachment_sighting"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM session"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM location_name"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM container_class"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM vehicle"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM attachment"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM place_evidence"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM ingest_correlation"));
        Assert.Equal("confirmed place", Scalar("SELECT name FROM place_name WHERE place_key = 'place:1'"));
        Assert.Equal("item", Scalar("SELECT display_name FROM wiki_item WHERE class_name = 'item_class'"));
        Assert.Equal("location", Scalar("SELECT name FROM wiki_location WHERE uuid = 'location-id'"));
        Assert.Equal("vehicle", Scalar("SELECT name FROM wiki_vehicle WHERE class_name = 'vehicle_class'"));
    }

    [Fact]
    public void Initialize_rolls_back_schema_transition_when_schema_creation_fails()
    {
        using (var cn = _db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE meta SET value = '15' WHERE key = 'schema_version';
                ALTER TABLE wiki_location RENAME TO saved_wiki_location;
                CREATE VIEW wiki_location AS SELECT * FROM saved_wiki_location;
                """;
            cmd.ExecuteNonQuery();
        }

        var beforeTables = SnapshotTables(DerivedTables.Append("meta"));
        var beforeSchema = SnapshotSchema();
        var error = Assert.Throws<SqliteException>(() => _db.Initialize());

        Assert.Contains("views may not be indexed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeTables, SnapshotTables(DerivedTables.Append("meta")));
        Assert.Equal(beforeSchema, SnapshotSchema());
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("42", Scalar("SELECT byte_offset FROM session WHERE path = 'seed.log'"));
        Assert.Equal("15", Scalar("SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal("location", Scalar("SELECT name FROM wiki_location WHERE uuid = 'location-id'"));
        Assert.Equal("view", Scalar("SELECT type FROM sqlite_master WHERE name = 'wiki_location'"));
        Assert.Equal("table", Scalar("SELECT type FROM sqlite_master WHERE name = 'move'"));
    }

    [Fact]
    public void Initialize_rolls_back_parser_transition_when_clear_fails()
    {
        using (var cn = _db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "UPDATE meta SET value = '0' WHERE key = 'parser_version';";
            cmd.ExecuteNonQuery();
        }
        var beforeTables = SnapshotTables(DerivedTables.Append("meta"));
        var beforeSchema = SnapshotSchema();
        using (var cn = _db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER block_parser_clear BEFORE DELETE ON session BEGIN SELECT RAISE(ABORT, 'parser injected'); END;";
            cmd.ExecuteNonQuery();
        }
        beforeSchema = SnapshotSchema();

        var error = Assert.Throws<SqliteException>(() => _db.Initialize());

        Assert.Contains("parser injected", error.Message, StringComparison.Ordinal);
        Assert.Equal(beforeTables, SnapshotTables(DerivedTables.Append("meta")));
        Assert.Equal(beforeSchema, SnapshotSchema());
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM attachment_sighting"));
        Assert.Equal("42", Scalar("SELECT byte_offset FROM session WHERE path = 'seed.log'"));
        Assert.Equal("0", Scalar("SELECT value FROM meta WHERE key = 'parser_version'"));
    }

    [Fact]
    public void Initialize_is_idempotent_and_preserves_rebuild_behavior()
    {
        _db.Initialize();
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM session"));

        using (var cn = _db.Open())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "UPDATE meta SET value = '15' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }

        _db.Initialize();

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM move"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM session"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM ingest_correlation"));
        Assert.Equal("confirmed place", Scalar("SELECT name FROM place_name WHERE place_key = 'place:1'"));
        Assert.Equal("item", Scalar("SELECT display_name FROM wiki_item WHERE class_name = 'item_class'"));
        Assert.Equal("17", Scalar("SELECT value FROM meta WHERE key = 'schema_version'"));
    }

    private void SeedDerivedData()
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session(id, path, byte_offset) VALUES(1, 'seed.log', 42);
            INSERT INTO ingest_correlation(session_id, state_version, byte_offset, generation, state_json) VALUES(1, 1, 42, 0, '{"version":1}');
            INSERT INTO move(id, session_id, line_offset, request_no, ts, move_type, item_class) VALUES(1, 1, 1, 1, '2026-10-13T00:00:00Z', 'Interaction', 'item_class');
            INSERT INTO attachment_sighting(session_id, line_offset, ts, geid, class_name, port, burst_id) VALUES(1, 2, '2026-10-13T00:00:00Z', 'geid', 'item_class', 'port', 2);
            INSERT INTO location_name(location_id, name) VALUES('location-id', 'derived location');
            INSERT INTO container_class(geid) VALUES('container-id');
            INSERT INTO vehicle(geid) VALUES('vehicle-id');
            INSERT INTO attachment(geid, class_name, last_seen) VALUES('attachment-id', 'item_class', '2026-10-13T00:00:00Z');
            INSERT INTO place_evidence(location_id, kind, value) VALUES('location-id', 'name', 'derived');
            INSERT INTO place_name(place_key, name, status, decided_by) VALUES('place:1', 'confirmed place', 'confirmed', 'user');
            INSERT INTO wiki_item(class_name, display_name) VALUES('item_class', 'item');
            INSERT INTO wiki_location(uuid, name, system) VALUES('location-id', 'location', 'system');
            INSERT INTO wiki_vehicle(class_name, name) VALUES('vehicle_class', 'vehicle');
            """;
        cmd.ExecuteNonQuery();
    }

    private static readonly string[] DerivedTables =
    [
        "attachment_sighting", "ingest_correlation", "move", "session", "location_name", "container_class",
        "vehicle", "attachment", "place_evidence",
    ];

    private string[] SnapshotTables(IEnumerable<string> tables)
    {
        using var cn = _db.Open();
        var snapshots = new List<string>();
        foreach (var table in tables)
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                snapshots.Add($"{table}|{string.Join("|", values)}");
            }
        }
        return snapshots.ToArray();
    }

    private string[] SnapshotSchema()
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT type || '|' || name || '|' || tbl_name || '|' || COALESCE(sql, '') FROM sqlite_master ORDER BY type, name";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private string? Scalar(string sql)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
