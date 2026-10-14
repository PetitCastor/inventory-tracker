using System.Net;
using System.Text;
using System.Text.Json;
using InventoryTracker.Data;
using InventoryTracker.Naming;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

public sealed class WikiNameServiceTests : IDisposable
{
    private const string PreviousStamp = "2026-10-13T12:00:00.0000000+00:00";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-wiki-items-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public WikiNameServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        _db.Initialize();
    }

    [Fact]
    public async Task Page_two_failure_preserves_the_previous_rows_stamp_and_loaded_cache()
    {
        SeedItem("old_item", "Old item");
        SetStamp(PreviousStamp);
        using var http = new HttpClient(new DelegateHandler((request, _) => Task.FromResult(
            Uri.UnescapeDataString(request.RequestUri!.Query).Contains("page[number]=1", StringComparison.Ordinal)
                ? Json(ItemPage("new_item", "New item", lastPage: 2))
                : new HttpResponseMessage(HttpStatusCode.InternalServerError))));
        var service = new WikiNameService(_db, http);

        Assert.Equal("Old item", service.Resolve("old_item").Display);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshAsync());

        Assert.Equal("Old item", service.Resolve("old_item").Display);
        Assert.Equal(NameMatch.None, service.Resolve("new_item").Match);
        Assert.Equal(PreviousStamp, GetStamp());
        Assert.Equal(1, CountItems());
        Assert.Equal(new Dictionary<string, string> { ["old_item"] = "Old item" }, ReadItemNames());
    }

    [Theory]
    [InlineData("{\"data\":[{\"class_name\":\"new_item\",\"name\":\"New item\"}]}")]
    [InlineData("{\"data\":[{\"class_name\":\"new_item\",\"name\":\"New item\"}],\"meta\":{}}")]
    public async Task Missing_pagination_metadata_preserves_the_previous_generation(string responseBody)
    {
        SeedItem("old_item", "Old item");
        SetStamp(PreviousStamp);
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(Json(responseBody))));
        var service = new WikiNameService(_db, http);
        Assert.Equal("Old item", service.Resolve("old_item").Display);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync());

        Assert.Equal("Old item", service.Resolve("old_item").Display);
        Assert.Equal(NameMatch.None, service.Resolve("new_item").Match);
        Assert.Equal(PreviousStamp, GetStamp());
        Assert.Equal(1, CountItems());
        Assert.Equal(new Dictionary<string, string> { ["old_item"] = "Old item" }, ReadItemNames());
    }

    [Fact]
    public async Task Cancellation_on_page_two_preserves_the_previous_rows_stamp_and_loaded_cache()
    {
        SeedItem("old_item", "Old item");
        SetStamp(PreviousStamp);
        var secondPageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            if (Uri.UnescapeDataString(request.RequestUri!.Query).Contains("page[number]=1", StringComparison.Ordinal))
                return Json(ItemPage("new_item", "New item", lastPage: 2));

            secondPageStarted.TrySetResult();
            await releaseSecondPage.Task.WaitAsync(ct);
            throw new InvalidOperationException("Unreachable");
        }));
        var service = new WikiNameService(_db, http);
        Assert.Equal("Old item", service.Resolve("old_item").Display);
        using var cts = new CancellationTokenSource();

        var refresh = service.RefreshAsync(ct: cts.Token);
        await secondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        Assert.Equal("Old item", service.Resolve("old_item").Display);
        Assert.Equal(NameMatch.None, service.Resolve("new_item").Match);
        Assert.Equal(PreviousStamp, GetStamp());
        Assert.Equal(1, CountItems());
        Assert.Equal(new Dictionary<string, string> { ["old_item"] = "Old item" }, ReadItemNames());
    }

    [Fact]
    public async Task Successful_empty_generation_replaces_rows_and_cache()
    {
        SeedItem("old_item", "Old item");
        SetStamp(PreviousStamp);
        using var http = new HttpClient(new DelegateHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/items", StringComparison.Ordinal)
                ? Json("{\"data\":[],\"meta\":{\"last_page\":1}}")
                : Json("{\"data\":[],\"meta\":{\"last_page\":1}}"))));
        var service = new WikiNameService(_db, http);
        Assert.Equal("Old item", service.Resolve("old_item").Display);

        var written = await service.RefreshAsync();

        Assert.Equal(0, written);
        Assert.Equal(NameMatch.None, service.Resolve("old_item").Match);
        Assert.Equal(0, CountItems());
        Assert.Empty(ReadItemNames());
        Assert.NotEqual(DateTimeOffset.Parse(PreviousStamp), service.LastRefresh());
    }

    [Fact]
    public async Task Successful_refresh_replaces_rows_and_refreshes_cache_loaded_during_download()
    {
        SeedItem("old_item", "Old item");
        SetStamp(PreviousStamp);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/items", StringComparison.Ordinal))
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(ct);
                return Json(ItemPage("new_item", "New item"));
            }

            return Json("{\"data\":[],\"meta\":{\"last_page\":1}}");
        }));
        var service = new WikiNameService(_db, http);

        var refresh = service.RefreshAsync();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Old item", service.Resolve("old_item").Display);
        releaseRequest.TrySetResult();
        await refresh;

        Assert.Equal(NameMatch.None, service.Resolve("old_item").Match);
        Assert.Equal("New item", service.Resolve("new_item").Display);
        Assert.Equal(1, CountItems());
        Assert.Equal(new Dictionary<string, string> { ["new_item"] = "New item" }, ReadItemNames());
        Assert.NotEqual(DateTimeOffset.Parse(PreviousStamp), service.LastRefresh());
    }

    [Fact]
    public async Task Concurrent_refreshes_of_the_same_catalogue_share_one_generation()
    {
        var firstItemRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstItemRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var itemRequests = 0;
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/items", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref itemRequests);
                firstItemRequest.TrySetResult();
                await releaseFirstItemRequest.Task.WaitAsync(ct);
                return Json(ItemPage("first_item", "First item"));
            }

            return Json("{\"data\":[],\"meta\":{\"last_page\":1}}");
        }));
        var service = new WikiNameService(_db, http);

        var first = service.RefreshAsync();
        await firstItemRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.RefreshAsync();
        var requestsWhileFirstBlocked = Volatile.Read(ref itemRequests);

        releaseFirstItemRequest.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, requestsWhileFirstBlocked);
        Assert.Equal(1, Volatile.Read(ref itemRequests));
        Assert.Equal(1, CountItems());
        Assert.Equal("First item", service.Resolve("first_item").Display);
    }

    private void SeedItem(string className, string displayName)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT INTO wiki_item(class_name, display_name) VALUES($c, $d)";
        cmd.Parameters.AddWithValue("$c", className);
        cmd.Parameters.AddWithValue("$d", displayName);
        cmd.ExecuteNonQuery();
    }

    private void SetStamp(string stamp)
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES('wiki_refreshed_at', $v)";
        cmd.Parameters.AddWithValue("$v", stamp);
        cmd.ExecuteNonQuery();
    }

    private string? GetStamp()
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'wiki_refreshed_at'";
        return cmd.ExecuteScalar() as string;
    }

    private int CountItems()
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM wiki_item";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private Dictionary<string, string> ReadItemNames()
    {
        using var cn = _db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT class_name, display_name FROM wiki_item";
        using var reader = cmd.ExecuteReader();
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) items.Add(reader.GetString(0), reader.GetString(1));
        return items;
    }

    private static string ItemPage(string className, string name, int lastPage = 1) =>
        JsonSerializer.Serialize(new
        {
            data = new[] { new { class_name = className, name } },
            meta = new { last_page = lastPage },
        });

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
