using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Data;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Naming;

/// <summary>A catalogue entry: the game's class name paired with what players call it.</summary>
public sealed record WikiItem(
    string ClassName,
    string? Uuid,
    string? DisplayName,
    string? Manufacturer,
    string? TypeLabel,
    string? ImageUrl,
    string? WebUrl);

/// <summary>How closely a class name matched the catalogue.</summary>
public enum NameMatch
{
    /// <summary>No catalogue entry; the raw class name is all we have.</summary>
    None,

    /// <summary>The class name is in the catalogue verbatim.</summary>
    Exact,

    /// <summary>Matched after dropping a trailing variant suffix such as "_red01".</summary>
    Variant,
}

public sealed record ResolvedName(string ClassName, string Display, NameMatch Match, WikiItem? Item);

/// <summary>
/// Maps game class names to human-readable names using the Star Citizen Wiki API.
/// <para>
/// The whole catalogue (~12k items) is pulled once into SQLite rather than queried per
/// item: the API's <c>filter[class_name]</c> is a substring match, so asking it about
/// "grin_multitool_01" returns 25 variants and never an exact answer. Locally we can
/// match precisely and search offline.
/// </para>
/// </summary>
public sealed class WikiNameService(TrackerDb db, HttpClient http, WikiRefreshCoordinator? refresh = null)
{
    private sealed class InflightRefresh
    {
        public TaskCompletionSource<int> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public int Waiters { get; set; }
        public bool Started { get; set; }
    }

    private const string ItemsUrl = "https://api.star-citizen.wiki/api/items";

    /// <summary>The API caps page size at 200 regardless of what is asked for.</summary>
    private const int PageSize = 200;

    /// <summary>How many trailing "_token" segments to strip before giving up on a match.</summary>
    private const int MaxSuffixDrops = 3;

    /// <summary>
    /// Guards <see cref="_cache"/>. The service is a singleton by design (it holds the whole
    /// ~12k-entry catalogue), so <see cref="Resolve"/> is called from Blazor circuit threads
    /// and from the background ingest thread while <see cref="RefreshAsync"/> may be
    /// invalidating from a third.
    /// </summary>
    private readonly Lock _cacheGate = new();
    private readonly object _refreshGate = new();
    private readonly WikiRefreshCoordinator _refresh = refresh ?? WikiRefreshCoordinator.Shared;

    internal WikiRefreshCoordinator RefreshCoordinator => _refresh;

    private Dictionary<string, WikiItem>? _cache;

    /// <summary>
    /// Downloads the full item catalogue before replacing the local copy. A failed or
    /// cancelled download leaves the previous generation and its cache intact.
    /// </summary>
    public async Task<int> RefreshAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        InflightRefresh refresh;
        var start = false;
        lock (_refreshGate)
        {
            refresh = _inflightRefresh ??= new InflightRefresh();
            refresh.Waiters++;
            if (!refresh.Started)
            {
                refresh.Started = true;
                start = true;
            }
        }

        if (start) _ = CompleteRefreshAsync(refresh, progress);
        try
        {
            return await refresh.Completion.Task.WaitAsync(ct);
        }
        finally
        {
            var waitForCleanup = false;
            lock (_refreshGate)
            {
                refresh.Waiters--;
                if (refresh.Waiters == 0 && ct.IsCancellationRequested && !refresh.Cleanup.Task.IsCompleted)
                {
                    if (!refresh.Completion.Task.IsCompleted) refresh.Cancellation.Cancel();
                    waitForCleanup = true;
                }
            }
            if (waitForCleanup)
            {
                await refresh.Cleanup.Task.ConfigureAwait(false);
                _ = refresh.Completion.Task.Exception;
            }
        }
    }

    private InflightRefresh? _inflightRefresh;

    private async Task CompleteRefreshAsync(
        InflightRefresh refresh,
        IProgress<string>? progress)
    {
        var ct = refresh.Cancellation.Token;
        var cancellationToken = refresh.Cancellation.Token;
        int? result = null;
        Exception? failure = null;
        var wasCanceled = false;
        try
        {
            var items = new List<WikiItem>();
            var page = 1;
            var lastPage = 1;

            do
            {
                ct.ThrowIfCancellationRequested();

                var url = $"{ItemsUrl}?page[size]={PageSize}&page[number]={page}";
                using var doc = await FetchJsonAsync(url, ct);

                if (!doc.RootElement.TryGetProperty("meta", out var meta) ||
                    meta.ValueKind != JsonValueKind.Object ||
                    !meta.TryGetProperty("last_page", out var lp) ||
                    lp.ValueKind != JsonValueKind.Number ||
                    !lp.TryGetInt32(out var reportedLastPage) || reportedLastPage < page)
                {
                    throw new InvalidDataException($"The item catalogue page {page} did not contain valid pagination metadata.");
                }

                lastPage = Math.Max(lastPage, reportedLastPage);

                var data = doc.RootElement.GetProperty("data");
                if (data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"The item catalogue page {page} did not contain an array.");

                foreach (var element in data.EnumerateArray())
                {
                    if (ReadItem(element) is { } item) items.Add(item);
                }

                progress?.Report($"page {page}/{lastPage} ({items.Count} items)");
                page++;

            }
            while (page <= lastPage);

            ct.ThrowIfCancellationRequested();
            var nextCache = new Dictionary<string, WikiItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items) nextCache[item.ClassName] = item;

            lock (_cacheGate)
            {
                using var cn = db.Open();
                using var tx = cn.BeginTransaction();
                using (var delete = cn.CreateCommand())
                {
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM wiki_item";
                    delete.ExecuteNonQuery();
                }

                foreach (var item in items) Upsert(cn, tx, item);
                Stamp(cn, tx, "wiki_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));
                tx.Commit();
                Volatile.Write(ref _cache, nextCache);
            }

            // Ships are named from the same wiki, so every place that refreshes the item names
            // refreshes theirs too. A ship the list does not name shows its class instead, and
            // the next refresh tries again.
            await new WikiVehicleCatalog(db, http, _refresh).RefreshAsync(progress, ct);

            result = items.Count;
        }
        catch (OperationCanceledException ex)
        {
            failure = ex;
            wasCanceled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (_refreshGate)
            {
                if (ReferenceEquals(_inflightRefresh, refresh)) _inflightRefresh = null;
                refresh.Cancellation.Dispose();
                if (wasCanceled) refresh.Completion.TrySetCanceled(cancellationToken);
                else if (failure is not null) refresh.Completion.TrySetException(failure);
                else refresh.Completion.TrySetResult(result!.Value);
                refresh.Cleanup.TrySetResult();
            }
        }
    }

    /// <summary>When the catalogue was last downloaded, or null if it never has been.</summary>
    public DateTimeOffset? LastRefresh()
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'wiki_refreshed_at'";
        return cmd.ExecuteScalar() is string s && DateTimeOffset.TryParse(s, out var ts) ? ts : null;
    }

    /// <summary>
    /// Resolves a game class name to something a player would recognise. Falls back to
    /// progressively dropping trailing variant segments — "grin_multitool_01_red01" is a
    /// colourway of "grin_multitool_01" and the catalogue only lists the base.
    /// </summary>
    public ResolvedName Resolve(string className)
    {
        var cache = Cache();

        if (cache.TryGetValue(className, out var exact))
        {
            return new ResolvedName(className, Display(exact, className), NameMatch.Exact, exact);
        }

        var stem = className;
        for (var drop = 0; drop < MaxSuffixDrops; drop++)
        {
            var cut = stem.LastIndexOf('_');

            // Keep at least two segments: "behr_rifle" is still a name, "behr" is not.
            if (cut <= 0 || stem.AsSpan(0, cut).Count('_') < 1) break;

            stem = stem[..cut];
            if (cache.TryGetValue(stem, out var variant))
            {
                return new ResolvedName(className, Display(variant, className), NameMatch.Variant, variant);
            }
        }

        return new ResolvedName(className, className, NameMatch.None, null);
    }

    private static string Display(WikiItem item, string fallback) =>
        string.IsNullOrWhiteSpace(item.DisplayName) ? fallback : item.DisplayName;

    private async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _refresh.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>(ct)
            ?? throw new InvalidOperationException($"Empty response from {url}");
    }

    private Dictionary<string, WikiItem> Cache()
    {
        // Fast path without the lock; the field is only ever published fully built.
        if (Volatile.Read(ref _cache) is { } ready) return ready;

        lock (_cacheGate)
        {
            if (_cache is not null) return _cache;

            var map = LoadCatalogue();
            Volatile.Write(ref _cache, map);
            return map;
        }
    }

    private Dictionary<string, WikiItem> LoadCatalogue()
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "SELECT class_name, uuid, display_name, manufacturer, type_label, image_url, web_url FROM wiki_item";

        var map = new Dictionary<string, WikiItem>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = new WikiItem(
                r.GetString(0),
                Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6));
        }

        return map;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static WikiItem? ReadItem(JsonElement e)
    {
        var className = Text(e, "class_name");
        if (string.IsNullOrWhiteSpace(className)) return null;

        string? manufacturer = null;
        if (e.TryGetProperty("manufacturer", out var man) && man.ValueKind == JsonValueKind.Object)
        {
            manufacturer = Text(man, "name");
        }

        string? image = null;
        if (e.TryGetProperty("images", out var images) &&
            images.ValueKind == JsonValueKind.Array &&
            images.GetArrayLength() > 0)
        {
            var first = images[0];
            image = first.ValueKind == JsonValueKind.Object ? Text(first, "url") : first.GetString();
        }

        return new WikiItem(
            className,
            Text(e, "uuid"),
            Text(e, "name"),
            manufacturer,
            Text(e, "type_label"),
            image,
            Text(e, "web_url"));
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Upsert(SqliteConnection cn, SqliteTransaction tx, WikiItem item)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO wiki_item(class_name, uuid, display_name, manufacturer, type_label,
                                  image_url, web_url, fetched_at)
            VALUES($c, $u, $d, $m, $t, $i, $w, $f)
            ON CONFLICT(class_name) DO UPDATE SET
                uuid = $u, display_name = $d, manufacturer = $m, type_label = $t,
                image_url = $i, web_url = $w, fetched_at = $f
            """;
        cmd.Parameters.AddWithValue("$c", item.ClassName);
        cmd.Parameters.AddWithValue("$u", (object?)item.Uuid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)item.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", (object?)item.Manufacturer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)item.TypeLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$i", (object?)item.ImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", (object?)item.WebUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void Stamp(SqliteConnection cn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
