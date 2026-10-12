using InventoryTracker.Data;
using InventoryTracker.Naming;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

public sealed class WikiVehicleCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-vehicles-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public WikiVehicleCatalogTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new TrackerDb(Path.Combine(_dir, "t.db"));
        _db.Initialize();
    }

    // Trimmed from the real /api/vehicles answer: the endpoint pages 50 at a time.
    private const string Page1 = """
        {"data":[
          {"class_name":"GLSN_Shiv","name":"Shiv","game_name":"Grey's Shiv","manufacturer":{"name":"Grey's Market"}},
          {"class_name":"ARGO_MOLE_BTALA","name":"MOLE Alliance","game_name":"ARGO MOLE Alliance"}
        ],"meta":{"last_page":2}}
        """;

    private const string Page2 = """
        {"data":[
          {"class_name":"ARGO_MOLE","name":"MOLE","game_name":"Argo MOLE"},
          {"class_name":"MISC_Nameless","name":"Nameless"}
        ],"meta":{"last_page":2}}
        """;

    [Fact]
    public async Task Every_page_is_read_and_each_class_keeps_its_own_name()
    {
        var handler = new StubJsonHandler(Page1, Page2);
        var written = await new WikiVehicleCatalog(_db, new HttpClient(handler)).RefreshAsync();

        Assert.Equal(4, written);
        Assert.Equal(2, handler.Requested.Count);

        using var cn = _db.Open();
        var names = WikiVehicleCatalog.Load(cn);

        Assert.Equal("Grey's Shiv", names["GLSN_Shiv"]);

        // The API's own filter would answer "ARGO_MOLE" with the Alliance edition too.
        Assert.Equal("Argo MOLE", names["ARGO_MOLE"]);
        Assert.Equal("ARGO MOLE Alliance", names["ARGO_MOLE_BTALA"]);

        // No game_name: the short name stands in.
        Assert.Equal("Nameless", names["MISC_Nameless"]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a pooled handle outlived the clear; the temp dir is harmless */ }
    }
}
