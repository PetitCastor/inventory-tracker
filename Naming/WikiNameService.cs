using System.Net.Http.Json;
using System.Text.Json;
using LogParser.Data;
using Microsoft.Data.Sqlite;

namespace LogParser.Naming;

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
public sealed class WikiNameService(TrackerDb db, HttpClient http)
{
    private const string ItemsUrl = "https://api.star-citizen.wiki/api/items";

    /// <summary>The API caps page size at 200 regardless of what is asked for.</summary>
    private const int PageSize = 200;

    /// <summary>The documented limit is 60/min; stay well under it to be a good citizen.</summary>
    private static readonly TimeSpan BetweenRequests = TimeSpan.FromMilliseconds(400);

    /// <summary>How many trailing "_token" segments to strip before giving up on a match.</summary>
    private const int MaxSuffixDrops = 3;

    private Dictionary<string, WikiItem>? _cache;

    /// <summary>
    /// Downloads the full item catalogue and replaces the local copy. Safe to re-run;
    /// existing rows are updated in place.
    /// </summary>
    public async Task<int> RefreshAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var cn = db.Open();
        var written = 0;
        var page = 1;
        var lastPage = 1;

        do
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{ItemsUrl}?page[size]={PageSize}&page[number]={page}";
            using var doc = await http.GetFromJsonAsync<JsonDocument>(url, ct)
                ?? throw new InvalidOperationException($"Empty response from {url}");

            if (doc.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("last_page", out var lp))
            {
                lastPage = lp.GetInt32();
            }

            using var tx = cn.BeginTransaction();
            foreach (var element in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (ReadItem(element) is { } item)
                {
                    Upsert(cn, tx, item);
                    written++;
                }
            }
            tx.Commit();

            progress?.Report($"page {page}/{lastPage} ({written} items)");
            page++;

            if (page <= lastPage) await Task.Delay(BetweenRequests, ct);
        }
        while (page <= lastPage);

        RebuildSearchIndex(cn);
        Stamp(cn, "wiki_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));
        _cache = null;

        return written;
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

    private Dictionary<string, WikiItem> Cache()
    {
        if (_cache is not null) return _cache;

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

        return _cache = map;
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

    private static void RebuildSearchIndex(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM wiki_item_fts;
            INSERT INTO wiki_item_fts(class_name, display_name, manufacturer, type_label)
            SELECT class_name,
                   COALESCE(display_name, ''),
                   COALESCE(manufacturer, ''),
                   COALESCE(type_label, '')
            FROM wiki_item;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void Stamp(SqliteConnection cn, string key, string value)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
