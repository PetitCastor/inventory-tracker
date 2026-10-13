using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Data;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Naming;

/// <summary>
/// What each ship class is called in game, from the Star Citizen Wiki API: "GLSN_Shiv" is
/// "Grey's Shiv".
/// <para>
/// The whole list (~310 vehicles) is pulled into SQLite, like the item catalogue and for the
/// same reason: <c>filter[class_name]</c> is a substring match, so asking about "ARGO_MOLE"
/// also returns "ARGO_MOLE_BTALA", the Alliance edition. Locally the match is exact.
/// </para>
/// </summary>
public sealed class WikiVehicleCatalog(TrackerDb db, HttpClient http)
{
    private const string VehiclesUrl = "https://api.star-citizen.wiki/api/vehicles";

    /// <summary>Asked for; the vehicles endpoint answers 50 a page whatever is asked.</summary>
    private const int PageSize = 200;

    /// <summary>
    /// The documented limit is 60/min, and this runs straight after the item catalogue's ~60
    /// requests, so it keeps to one a second: seven pages cost seven seconds.
    /// </summary>
    private static readonly TimeSpan BetweenRequests = TimeSpan.FromSeconds(1);

    /// <summary>Downloads the vehicle list and replaces the local copy. Safe to re-run.</summary>
    public async Task<int> RefreshAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var cn = db.Open();
        var written = 0;
        var page = 1;
        var lastPage = 1;

        do
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{VehiclesUrl}?page[size]={PageSize}&page[number]={page}";
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
                if (Text(element, "class_name") is not { Length: > 0 } className) continue;

                Upsert(cn, tx, className, Text(element, "game_name"), Text(element, "name"),
                    element.TryGetProperty("manufacturer", out var man) && man.ValueKind == JsonValueKind.Object
                        ? Text(man, "name")
                        : null,
                    Text(element, "web_url"));
                written++;
            }
            tx.Commit();

            progress?.Report($"vehicles page {page}/{lastPage} ({written} vehicles)");
            page++;

            if (page <= lastPage) await Task.Delay(BetweenRequests, ct);
        }
        while (page <= lastPage);

        return written;
    }

    /// <summary>
    /// Every ship class the catalogue knows, mapped to its in-game name — "Grey's Shiv" rather
    /// than the shorter "Shiv", since that is what the game's own menus show.
    /// </summary>
    public static Dictionary<string, string> Load(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT class_name, COALESCE(game_name, name) FROM wiki_vehicle";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!r.IsDBNull(1)) map[r.GetString(0)] = r.GetString(1);
        }

        return map;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Upsert(
        SqliteConnection cn, SqliteTransaction tx,
        string className, string? gameName, string? name, string? manufacturer, string? webUrl)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO wiki_vehicle(class_name, game_name, name, manufacturer, web_url, fetched_at)
            VALUES($c, $g, $n, $m, $w, $f)
            ON CONFLICT(class_name) DO UPDATE SET
                game_name = $g, name = $n, manufacturer = $m, web_url = $w, fetched_at = $f
            """;
        cmd.Parameters.AddWithValue("$c", className);
        cmd.Parameters.AddWithValue("$g", (object?)gameName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", (object?)manufacturer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", (object?)webUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
