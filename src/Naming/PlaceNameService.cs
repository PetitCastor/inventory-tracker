using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.App;
using InventoryTracker.Data;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Naming;

/// <summary>
/// Files a name for every place the logs reveal, and keeps it until it is confirmed.
/// <para>
/// After an ingest pass, every place whose name is not yet confirmed is resolved again by
/// <see cref="PlaceNameResolver"/>, against the game's own text (<see cref="GameLocalization"/>)
/// and the star-citizen.wiki location list, which this service downloads and caches. The
/// result goes to <c>place_name</c>, which <see cref="PlaceCatalog"/> reads: a confirmed row is
/// never touched again, so arriving somewhere new costs one resolution and the name then stays
/// put. The player can confirm or correct any name, and that is final too.
/// </para>
/// </summary>
public sealed class PlaceNameService(TrackerDb db, TrackerOptions options, HttpClient http)
{
    private const string LocationsUrl = "https://api.star-citizen.wiki/api/locations";

    /// <summary>The API caps page size at 200 regardless of what is asked for.</summary>
    private const int PageSize = 200;

    /// <summary>Same courtesy delay as <see cref="WikiNameService"/>.</summary>
    private static readonly TimeSpan BetweenRequests = TimeSpan.FromMilliseconds(400);

    private readonly Lock _textGate = new();
    private (string Path, DateTime Written, GameLocalization Text)? _text;

    /// <summary>
    /// Downloads the wiki's location list and replaces the local copy (~1,970 places, ten
    /// requests). Nothing is written until every page is in: a resolution run against half a
    /// list could miss the rival name that should have kept a place unconfirmed, and a
    /// confirmation is final. Places the wiki no longer lists are dropped with the swap.
    /// </summary>
    public async Task<int> RefreshAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rows = new List<(string Uuid, WikiLocation Location)>();
        var page = 1;
        var lastPage = 1;

        do
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{LocationsUrl}?page[size]={PageSize}&page[number]={page}";
            using var doc = await http.GetFromJsonAsync<JsonDocument>(url, ct)
                ?? throw new InvalidOperationException($"Empty response from {url}");

            if (doc.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("last_page", out var lp))
            {
                lastPage = lp.GetInt32();
            }

            foreach (var element in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (ReadLocation(element) is { } row) rows.Add(row);
            }

            progress?.Report($"places page {page}/{lastPage} ({rows.Count} places)");
            page++;

            if (page <= lastPage) await Task.Delay(BetweenRequests, ct);
        }
        while (page <= lastPage);

        using var cn = db.Open();
        using (var tx = cn.BeginTransaction())
        {
            using (var clear = cn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM wiki_location";
                clear.ExecuteNonQuery();
            }

            foreach (var (uuid, location) in rows) Insert(cn, tx, uuid, location);
            tx.Commit();
        }

        Stamp(cn, "wiki_locations_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));
        return rows.Count;
    }

    /// <summary>When the location list was last downloaded, or null if it never has been.</summary>
    public DateTimeOffset? LastRefresh()
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'wiki_locations_refreshed_at'";
        return cmd.ExecuteScalar() is string s && DateTimeOffset.TryParse(s, out var ts) ? ts : null;
    }

    /// <summary>
    /// Resolves every place whose name is not confirmed yet and files the result. Returns
    /// how many places' names changed, so the caller knows whether the view is stale.
    /// </summary>
    public int ResolvePlaces()
    {
        using var cn = db.Open();

        var text = GameText();
        var wiki = WikiLocations.Load(cn);
        var filed = PlaceCatalog.LoadDecisions(cn);
        var changed = 0;

        // Every place, not just the stations: the ids seen in passing ("Hurston", a mining
        // site) end up in item chains too, and are held to the same rule.
        foreach (var place in PlaceCatalog.Load(cn).All)
        {
            var key = place.Key;
            if (filed.TryGetValue(key, out var was) && was.Status == PlaceNameStatus.Confirmed) continue;

            var now = PlaceNameResolver.Resolve(place.RawName, place.System, place.LoggedName, text, wiki);
            if (was == now) continue;

            Write(cn, key, now, decidedBy: "auto");
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// The player's word on a place's name: confirms <paramref name="name"/> for good, whether
    /// it is the proposed name or a correction. The resolver never revisits it.
    /// </summary>
    public void Confirm(Place place, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A place name cannot be blank.", nameof(name));

        using var cn = db.Open();
        var onWiki = WikiLocations.Load(cn).Find(name, place.System);

        var sources = PlaceNameSource.User;
        if (string.Equals(name, place.Name, StringComparison.OrdinalIgnoreCase)) sources |= place.NameSources;
        if (onWiki is not null) sources |= PlaceNameSource.Wiki;

        Write(cn, place.Key, new PlaceResolution(onWiki?.Name ?? name, PlaceNameStatus.Confirmed, sources, onWiki?.Planet),
            decidedBy: "user");
    }

    /// <summary>Names the player could mean for a place in <paramref name="system"/>, for
    /// suggesting a correction.</summary>
    public IReadOnlyList<string> Suggestions(string system)
    {
        using var cn = db.Open();
        return WikiLocations.Load(cn).NamesIn(system);
    }

    /// <summary>The game's text, re-read only when the file changes. The game rewrites it on
    /// patch day, and the log directory can be changed in Settings.</summary>
    private GameLocalization GameText()
    {
        try
        {
            var path = GameLocalization.PathFor(options.LogDir);
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

            lock (_textGate)
            {
                if (_text is { } cached && cached.Path == path && cached.Written == written) return cached.Text;

                var text = written == DateTime.MinValue ? GameLocalization.Empty : GameLocalization.Load(options.LogDir);
                _text = (path, written, text);
                return text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mid-patch, or a log directory on a drive that went away: names wait for the next pass.
            return GameLocalization.Empty;
        }
    }

    internal static PlaceNameStatus ParseStatus(string status) => status switch
    {
        "confirmed" => PlaceNameStatus.Confirmed,
        "pending" => PlaceNameStatus.Pending,
        _ => PlaceNameStatus.Unresolved,
    };

    private static string StatusText(PlaceNameStatus status) => status switch
    {
        PlaceNameStatus.Confirmed => "confirmed",
        PlaceNameStatus.Pending => "pending",
        _ => "unresolved",
    };

    /// <summary>
    /// Files a decision. An automatic one never overwrites a confirmed row: the player can
    /// confirm a name from the page while an ingest pass is resolving the same place, and the
    /// guard in the statement itself is what keeps the player's word from being lost.
    /// </summary>
    private static void Write(SqliteConnection cn, string key, PlaceResolution resolution, string decidedBy)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO place_name(place_key, name, status, sources, planet, decided_by, updated_at)
            VALUES($k, $n, $s, $src, $p, $d, $t)
            ON CONFLICT(place_key) DO UPDATE SET
                name = $n, status = $s, sources = $src, planet = $p, decided_by = $d, updated_at = $t
            WHERE $d = 'user' OR place_name.status <> 'confirmed'
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$n", (object?)resolution.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", StatusText(resolution.Status));
        cmd.Parameters.AddWithValue("$src", (int)resolution.Sources);
        cmd.Parameters.AddWithValue("$p", (object?)resolution.Planet ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", decidedBy);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static (string Uuid, WikiLocation Location)? ReadLocation(JsonElement e)
    {
        var uuid = Text(e, "uuid");
        var name = Text(e, "name");
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(name)) return null;

        // "Nyx System" on the list; "Nyx" everywhere else in the tracker.
        var system = Text(e, "system") ?? Place.UnknownSystem;
        if (system.EndsWith(" System", StringComparison.OrdinalIgnoreCase)) system = system[..^" System".Length].Trim();
        if (Systems.IsSystem(system)) system = Systems.Canonical(system);

        string? parent = null, parentType = null, type = null;
        if (e.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            parent = Text(p, "name");
            parentType = Text(p, "type_name");
        }
        if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Object) type = Text(t, "name");

        return (uuid, new WikiLocation(name.Trim(), system, parent, parentType, type));
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Insert(SqliteConnection cn, SqliteTransaction tx, string uuid, WikiLocation l)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO wiki_location(uuid, name, system, parent, parent_type, type, fetched_at)
            VALUES($u, $n, $s, $p, $pt, $t, $f)
            ON CONFLICT(uuid) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$u", uuid);
        cmd.Parameters.AddWithValue("$n", l.Name);
        cmd.Parameters.AddWithValue("$s", l.System);
        cmd.Parameters.AddWithValue("$p", (object?)l.Parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pt", (object?)l.ParentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)l.Type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", DateTimeOffset.UtcNow.ToString("O"));
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
