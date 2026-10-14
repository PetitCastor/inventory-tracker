using System.Net;
using System.Text;
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

    [Fact]
    public async Task Page_two_failure_keeps_the_previous_catalogue_intact()
    {
        SeedVehicle("old_vehicle", "Old vehicle");
        using var http = new HttpClient(new DelegateHandler((request, _) => Task.FromResult(
            Uri.UnescapeDataString(request.RequestUri!.Query).Contains("page[number]=1", StringComparison.Ordinal)
                ? Json("{\"data\":[{\"class_name\":\"new_vehicle\",\"name\":\"New vehicle\"}],\"meta\":{\"last_page\":2}}")
                : new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        await Assert.ThrowsAsync<HttpRequestException>(() => new WikiVehicleCatalog(_db, http).RefreshAsync());

        using var cn = _db.Open();
        var names = WikiVehicleCatalog.Load(cn);
        Assert.Single(names);
        Assert.Equal("Old vehicle", names["old_vehicle"]);
        Assert.False(names.ContainsKey("new_vehicle"));
    }

    [Theory]
    [InlineData("{\"data\":[{\"class_name\":\"new_vehicle\",\"name\":\"New vehicle\"}]}")]
    [InlineData("{\"data\":[{\"class_name\":\"new_vehicle\",\"name\":\"New vehicle\"}],\"meta\":{}}")]
    public async Task Missing_pagination_metadata_keeps_the_previous_catalogue_intact(string responseBody)
    {
        SeedVehicle("old_vehicle", "Old vehicle");
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(Json(responseBody))));

        await Assert.ThrowsAsync<InvalidDataException>(() => new WikiVehicleCatalog(_db, http).RefreshAsync());

        using var cn = _db.Open();
        var names = WikiVehicleCatalog.Load(cn);
        Assert.Single(names);
        Assert.Equal("Old vehicle", names["old_vehicle"]);
        Assert.False(names.ContainsKey("new_vehicle"));
        Assert.Equal(1, CountVehicles());
    }

    [Fact]
    public async Task Cancellation_on_page_two_keeps_the_previous_catalogue_intact()
    {
        SeedVehicle("old_vehicle", "Old vehicle");
        var secondPageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            if (Uri.UnescapeDataString(request.RequestUri!.Query).Contains("page[number]=1", StringComparison.Ordinal))
                return Json("{\"data\":[{\"class_name\":\"new_vehicle\",\"name\":\"New vehicle\"}],\"meta\":{\"last_page\":2}}");

            secondPageStarted.TrySetResult();
            await releaseSecondPage.Task.WaitAsync(ct);
            throw new InvalidOperationException("Unreachable");
        }));
        using var cts = new CancellationTokenSource();

        var refresh = new WikiVehicleCatalog(_db, http).RefreshAsync(ct: cts.Token);
        await secondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        using var cn = _db.Open();
        var names = WikiVehicleCatalog.Load(cn);
        Assert.Single(names);
        Assert.Equal("Old vehicle", names["old_vehicle"]);
        Assert.False(names.ContainsKey("new_vehicle"));
        Assert.Equal(1, CountVehicles());
    }

    [Fact]
    public async Task Successful_empty_generation_removes_all_vehicles()
    {
        SeedVehicle("old_vehicle", "Old vehicle");
        using var http = new HttpClient(new StubJsonHandler(
            "{\"data\":[],\"meta\":{\"last_page\":1}}"));

        var written = await new WikiVehicleCatalog(_db, http).RefreshAsync();

        Assert.Equal(0, written);
        using var cn = _db.Open();
        Assert.Empty(WikiVehicleCatalog.Load(cn));
        Assert.Equal(0, CountVehicles());
    }

    [Fact]
    public async Task Successful_refresh_removes_vehicles_absent_from_the_new_generation()
    {
        SeedVehicle("old_vehicle", "Old vehicle");
        using var http = new HttpClient(new StubJsonHandler(
            "{\"data\":[{\"class_name\":\"new_vehicle\",\"name\":\"New vehicle\"}],\"meta\":{\"last_page\":1}}"));

        await new WikiVehicleCatalog(_db, http).RefreshAsync();

        using var cn = _db.Open();
        var names = WikiVehicleCatalog.Load(cn);
        Assert.Single(names);
        Assert.Equal("New vehicle", names["new_vehicle"]);
        Assert.False(names.ContainsKey("old_vehicle"));
    }

    [Fact]
    public async Task Concurrent_refreshes_of_the_same_catalogue_are_serialized()
    {
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler(async (_, ct) =>
        {
            var number = Interlocked.Increment(ref requests);
            if (number == 1)
            {
                firstRequest.TrySetResult();
                await releaseFirstRequest.Task.WaitAsync(ct);
                return Json("{\"data\":[{\"class_name\":\"first_vehicle\",\"name\":\"First vehicle\"}],\"meta\":{\"last_page\":1}}");
            }

            return Json("{\"data\":[{\"class_name\":\"second_vehicle\",\"name\":\"Second vehicle\"}],\"meta\":{\"last_page\":1}}");
        }));
        var catalog = new WikiVehicleCatalog(_db, http);

        var first = catalog.RefreshAsync();
        await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = catalog.RefreshAsync();
        var requestsWhileFirstBlocked = Volatile.Read(ref requests);

        releaseFirstRequest.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, requestsWhileFirstBlocked);
        Assert.Equal(2, Volatile.Read(ref requests));
        using var cn = _db.Open();
        var names = WikiVehicleCatalog.Load(cn);
        Assert.Single(names);
        Assert.Equal("Second vehicle", names["second_vehicle"]);
    }

    private void SeedVehicle(string className, string name)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT INTO wiki_vehicle(class_name, name) VALUES($c, $n)";
        cmd.Parameters.AddWithValue("$c", className);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    private int CountVehicles()
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM wiki_vehicle";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }
}
