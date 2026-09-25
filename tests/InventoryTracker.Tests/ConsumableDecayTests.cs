using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Resolve;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

/// <summary>
/// Food and drink are used up straight out of a backpack without a log line, so one left
/// untouched for a week is doubted — the cruz bottles of the 2026-09-25 playtest.
/// </summary>
public sealed class ConsumableDecayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public ConsumableDecayTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        _db.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTimeOffset Stored = new(2026, 8, 25, 17, 34, 6, TimeSpan.Zero);

    /// <summary>A named Store, as the game wrote the one of 2026-08-25.</summary>
    private void Store(string item, string target) => File.AppendAllText(
        Path.Combine(_dir, "Game.log"),
        "<2026-08-25T17:34:06.000Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[12] Type[Store] " +
        $"for 'Pilot' [204821708183] Source Inventory[INVALID] Target Inventory[{target}]. " +
        "Source[NONE] amount[1] rank[0]. Target[NONE] amount[0] rank[0]. " +
        $"Item[{item}] action[None]. RequestInProgress[0] CurrentProcess[]\n" +
        "<2026-08-25T17:34:06.500Z> [Notice] <Inventory Request Completed> Request[12] Player[Pilot] " +
        "Result[succeed] Elapsed[0.5] PendingMoves[0]\n");

    private ItemHolding Resolve(TimeSpan after)
    {
        new LogIngestor(_db, _dir).IngestAll();
        return Assert.Single(HoldingResolver.Load(_db, Stored + after).ResolveAll());
    }

    [Fact]
    public void A_drink_left_in_a_backpack_for_a_month_may_be_gone()
    {
        Store("Drink_bottle_cruz_01_a_780590795173", "723713568285:Container:0");

        var drink = Resolve(TimeSpan.FromDays(31));

        Assert.Equal(Confidence.Low, drink.Confidence);
        Assert.Contains(drink.Caveats, c => c.Contains("used up without a log line", StringComparison.Ordinal)
                                            && c.Contains("31 days", StringComparison.Ordinal));
    }

    [Fact]
    public void A_drink_stored_yesterday_is_not_doubted()
    {
        Store("Drink_bottle_cruz_01_a_780590795173", "723713568285:Container:0");

        var drink = Resolve(TimeSpan.FromDays(1));

        Assert.DoesNotContain(drink.Caveats, c => c.Contains("may be gone", StringComparison.Ordinal));
    }

    [Fact]
    public void A_medpen_lying_in_a_station_inventory_is_not_doubted()
    {
        // Taking anything out of a station's inventory is a logged move.
        Store("crlf_consumable_healing_01_783802033951", "204821708183:Location:2273540638");

        var medpen = Resolve(TimeSpan.FromDays(29));

        Assert.DoesNotContain(medpen.Caveats, c => c.Contains("may be gone", StringComparison.Ordinal));
    }
}
