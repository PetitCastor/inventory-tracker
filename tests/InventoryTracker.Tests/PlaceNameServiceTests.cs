using InventoryTracker.App;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Naming;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

public sealed class PlaceNameServiceTests : IDisposable
{
    private const string StationId = "3058615591";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;
    private readonly HttpClient _http = new();
    private readonly PlaceNameService _places;

    public PlaceNameServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        _db.Initialize();
        _places = new PlaceNameService(_db, new TrackerOptions { LogDir = _dir }, _http);
    }

    public void Dispose()
    {
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The player spawns at a belt station the game names nowhere in the log.</summary>
    private void SpawnAtKeeger001()
    {
        File.WriteAllText(Path.Combine(_dir, "Game.log"), string.Join("\n",
            "<2026-10-10T19:02:30.000Z> [Notice] <X> first line",
            $"<2026-10-10T19:02:34.554Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. Landing [0] -> [{StationId}]. Location [0] -> [{StationId}]. Pending [0]",
            "<2026-10-10T19:02:34.557Z> [Notice] <RequestLocationInventory> Player[Pilot] requested inventory for Location[AsteroidClusterBase_Nyx_Social_Keeger_001]") + "\n");

        new LogIngestor(_db, _dir).IngestAll();
    }

    private void GameText(string text)
    {
        var folder = Path.Combine(_dir, "data", "Localization", "english");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "global.ini");
        File.WriteAllText(path, text);

        // The service re-reads the file when its timestamp moves; two writes in quick
        // succession must not share one.
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++_textVersion));
    }

    private int _textVersion;

    private void OnWiki(string name, string system)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO wiki_location(uuid, name, system, parent, parent_type, type)
            VALUES($u, $n, $s, $s, 'Star', 'Manmade')
            """;
        cmd.Parameters.AddWithValue("$u", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$s", system);
        cmd.ExecuteNonQuery();
    }

    private Place Station() => PlaceCatalog.Load(_db).ByLocationId(StationId)!;

    [Fact]
    public void A_station_the_logs_never_name_is_shown_as_logged_until_resolved()
    {
        SpawnAtKeeger001();

        var place = Station();

        Assert.Equal("AsteroidClusterBase_Nyx_Social_Keeger_001", place.Name);
        Assert.Equal(PlaceNameStatus.Unresolved, place.NameStatus);
        Assert.Equal("Nyx", place.System);
    }

    [Fact]
    public void A_new_station_is_resolved_from_the_games_text_and_confirmed_by_the_wiki()
    {
        GameText("Nyx_SocialStation_001=People's Service Station Alpha\r\n");
        OnWiki("People's Service Station Alpha", "Nyx");
        SpawnAtKeeger001();

        Assert.Equal(1, _places.ResolvePlaces());

        var place = Station();
        Assert.Equal("People's Service Station Alpha", place.Name);
        Assert.Equal(PlaceNameStatus.Confirmed, place.NameStatus);
        Assert.Equal(0, _places.ResolvePlaces()); // nothing new to decide
    }

    [Fact]
    public void A_proposal_waits_for_the_wiki_and_is_confirmed_once_it_backs_it()
    {
        GameText("Nyx_SocialStation_001=People's Service Station Alpha\r\n");
        SpawnAtKeeger001();

        _places.ResolvePlaces();
        Assert.Equal(PlaceNameStatus.Pending, Station().NameStatus);

        OnWiki("People's Service Station Alpha", "Nyx");
        _places.ResolvePlaces();
        Assert.Equal(PlaceNameStatus.Confirmed, Station().NameStatus);
    }

    [Fact]
    public void A_confirmed_name_is_never_resolved_again()
    {
        GameText("Nyx_SocialStation_001=People's Service Station Alpha\r\n");
        OnWiki("People's Service Station Alpha", "Nyx");
        SpawnAtKeeger001();
        _places.ResolvePlaces();

        // A later patch renames the entry; the station keeps the name it was confirmed under.
        GameText("Nyx_SocialStation_001=Something Else\r\n");
        OnWiki("Something Else", "Nyx");

        Assert.Equal(0, _places.ResolvePlaces());
        Assert.Equal("People's Service Station Alpha", Station().Name);
    }

    [Fact]
    public void A_place_nothing_in_the_logs_names_takes_the_name_the_player_gives_it()
    {
        _places.Confirm(PlaceCatalog.Unnamed("777"), "Grim HEX");

        var place = PlaceCatalog.Load(_db).ByLocationId("777");

        Assert.NotNull(place);
        Assert.Equal("Grim HEX", place.Name);
        Assert.Equal(PlaceNameStatus.Confirmed, place.NameStatus);
    }

    [Fact]
    public void The_players_correction_is_final_and_survives_a_rebuild()
    {
        SpawnAtKeeger001();
        _places.ResolvePlaces();

        _places.Confirm(Station(), "People's Service Station Alpha");

        _db.ResetIngest();
        SpawnAtKeeger001();
        GameText("Nyx_SocialStation_001=Something Else\r\n");
        _places.ResolvePlaces();

        var place = Station();
        Assert.Equal("People's Service Station Alpha", place.Name);
        Assert.Equal(PlaceNameStatus.Confirmed, place.NameStatus);
        Assert.True(place.NameSources.HasFlag(PlaceNameSource.User));
    }
}
