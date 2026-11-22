using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Model;
using InventoryTracker.Resolve;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

/// <summary>
/// A death that leaves a body: the game lists what hung on it, and the tracker moves that, and
/// what was inside it, onto the corpse. Lines are from the player's death of 2026-11-21 at
/// 19:53:34, trimmed of the player name.
/// </summary>
public sealed class DeathTests : IDisposable
{
    private const string BackpackGeid = "845736965831";
    private const string Backpack = "qrt_utility_heavy_backpack_01_01_03";

    private static string CorpseLine(string entity, string cls, string port) =>
        "<2026-11-21T19:53:34.297Z> [Notice] <Adding non kept item [CSCActorCorpseUtils::PopulateItemPortForItemRecoveryEntitlement]> " +
        $"Item '{entity} - Class({cls}) - Context(Streamable Runtime-spawned) - Socpak()', Recorded data is: " +
        $"Port Name '{port}', Class GUID: 'aa3a3eeb-9d03-414e-8ef2-0988a9b88fb6' [Team_CoreGameplayFeatures][Unknown]";

    private static readonly string[] DeathLog =
    [
        "<2026-11-21T18:56:10.621Z> [Notice] <Update Inventory Location> Player [Pilot] is changing location. " +
        "Landing [0] -> [2273540638]. Location [0] -> [2273540638]. Pending [0] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-21T18:56:10.621Z> [Notice] <AttachmentReceived> Player[Pilot] Attachment[qrt_utility_heavy_backpack_01_01_03_845736965831, " +
        "qrt_utility_heavy_backpack_01_01_03, 845736965831] Status[persistent] Port[backpack] Elapsed[0.000018] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-21T19:48:36.999Z> [Notice] <Inventory Mgmt Request Queued> Queued Request[98] Type[Store] for 'Pilot' [204821708183] " +
        "Source Inventory[INVALID] Target Inventory[845736965831:Container:0]. Source[NULL] amount[0] rank[]. " +
        "Target[NULL] amount[0] rank[amsramwemoaazn]. Item[none_rifle_multi_01_945948376212] action[None]. " +
        "RequestInProgress[0] CurrentProcess[] [Team_CoreGameplayFeatures][Inventory]",
        "<2026-11-21T19:48:37.753Z> [Notice] <Inventory Request Completed> Request[98] Player[Pilot] Result[succeed] " +
        "Elapsed[0.755424] PendingMoves[1] [Team_CoreGameplayFeatures][Inventory]",
        CorpseLine("body_01_noMagicPocket_926157411032", "body_01_noMagicPocket", "Body_ItemPort"),
        CorpseLine("qrt_utility_heavy_backpack_01_01_03_845736965831", Backpack, "backpack"),
        "<2026-11-21T19:53:36.235Z> [Notice] <AttachmentReceived> Player[Pilot] Attachment[qrt_utility_heavy_backpack_01_01_03_946952444689, " +
        "qrt_utility_heavy_backpack_01_01_03, 946952444689] Status[persistent] Port[backpack] Elapsed[0.000053] [Team_CoreGameplayFeatures][Inventory]",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;

    public DeathTests()
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

    private static InventoryEvent? Event(string raw)
    {
        Assert.True(LogLine.TryParse(raw, out var line));
        return InventoryEventParser.Parse(line);
    }

    [Fact]
    public void Reads_a_corpse_line_as_an_item_left_on_the_body()
    {
        var ev = Assert.IsType<CorpseItemListed>(Event(CorpseLine(
            "behr_rifle_ballistic_03_mag_946063332475", "behr_rifle_ballistic_03_mag", "magazine_attach_1")));

        Assert.Equal("946063332475", ev.Geid);
        Assert.Equal("behr_rifle_ballistic_03_mag", ev.ClassName);
        Assert.Equal("magazine_attach_1", ev.Port);
    }

    [Fact]
    public void A_corpse_key_reads_back_when_and_where()
    {
        var at = DateTimeOffset.Parse("2026-11-21T19:53:34.297Z");

        Assert.True(CorpseKey.TryParse(CorpseKey.Format(at, "2273540638"), out var died, out var where));
        Assert.Equal(at, died);
        Assert.Equal("2273540638", where);

        Assert.True(CorpseKey.TryParse(CorpseKey.Format(at, null), out _, out var nowhere));
        Assert.Equal("", nowhere);
        Assert.False(CorpseKey.TryParse("2273540638", out _, out _));
    }

    [Fact]
    public void What_was_in_the_backpack_is_lost_with_it_and_the_new_backpack_is_empty()
    {
        File.WriteAllText(Path.Combine(_dir, "Game.log"), string.Join("\n", DeathLog) + "\n");

        var stats = new LogIngestor(_db, _dir).IngestAll();

        // The body is the character, not an item: only the backpack is recorded.
        Assert.Equal(1, stats.DeathItemsRecorded);
        Assert.True(stats.ChangedHoldings);

        var resolver = HoldingResolver.Load(_db, DateTimeOffset.Parse("2026-11-21T20:00:00Z"));
        var rifle = resolver.Resolve("945948376212");
        Assert.NotNull(rifle);
        Assert.True(rifle.IsLostAtDeath);
        Assert.Equal("on your body at location 2273540638", rifle.Root!.Label);
        Assert.Contains(rifle.Caveats, c => c.StartsWith("left on your body when you died on 2026-11-21 19:53 UTC", StringComparison.Ordinal));

        Assert.True(resolver.Resolve(BackpackGeid)!.IsLostAtDeath);
        Assert.False(resolver.Resolve("946952444689")!.IsLostAtDeath);
        Assert.DoesNotContain(resolver.ResolveAll(), h => h.ItemClass == "body_01_noMagicPocket");
    }

    [Fact]
    public void A_rifle_left_on_the_body_keeps_its_magazine_as_a_part_not_an_item_of_its_own()
    {
        // The rifle comes on with its magazine in one burst, and the corpse lists both, as a
        // real corpse listing of 2026-11-21 does (its geids are reused here).
        string Worn(string ts, string entity, string cls, string geid, string port) =>
            $"<2026-11-21T{ts}Z> [Notice] <AttachmentReceived> Player[Pilot] Attachment[{entity}, {cls}, {geid}] " +
            $"Status[persistent] Port[{port}] Elapsed[0.000018] [Team_CoreGameplayFeatures][Inventory]";

        string[] log =
        [
            DeathLog[0],
            Worn("18:56:10.621", "behr_rifle_ballistic_03_784862306928", "behr_rifle_ballistic_03", "784862306928", "wep_stocked_3"),
            Worn("18:56:10.622", "behr_rifle_ballistic_03_mag_784862306929", "behr_rifle_ballistic_03_mag", "784862306929", "magazine_attach"),
            CorpseLine("behr_rifle_ballistic_03_784862306928", "behr_rifle_ballistic_03", "wep_stocked_3"),
            CorpseLine("behr_rifle_ballistic_03_mag_784862306929", "behr_rifle_ballistic_03_mag", "magazine_attach"),
        ];
        File.WriteAllText(Path.Combine(_dir, "Game.log"), string.Join("\n", log) + "\n");
        new LogIngestor(_db, _dir).IngestAll();

        var resolver = HoldingResolver.Load(_db, DateTimeOffset.Parse("2026-11-21T20:00:00Z"));
        var rifle = resolver.Resolve("784862306928");
        var magazine = resolver.Resolve("784862306929");
        Assert.NotNull(rifle);
        Assert.NotNull(magazine);

        Assert.True(rifle.IsLostAtDeath);
        Assert.False(resolver.IsPart(rifle));
        Assert.True(resolver.HasParts(rifle));

        Assert.True(magazine.IsLostAtDeath);
        Assert.True(resolver.IsPart(magazine));
        Assert.Equal("784862306928", magazine.Chain[0].Key);
    }

    // ---- The ledger on its own ----

    private static readonly Holding Body = new(InventoryKind.Equipped, "");
    private static readonly Holding Station = new(InventoryKind.Location, "4005457614");
    private static readonly string CorpseAt = CorpseKey.Format(At(10), "4005457614");
    private static readonly Holding Corpse = new(InventoryKind.Corpse, CorpseAt);
    private static readonly Holding BackpackHolding = new(InventoryKind.Container, BackpackGeid);
    private const string ArmorGeid = "845736965830";
    private static readonly Holding Armor = new(InventoryKind.Container, ArmorGeid);

    private static int _id;

    private static DateTimeOffset At(int minute) => new(2026, 11, 21, 19, minute, 0, TimeSpan.Zero);

    private static MoveRecord Move(Holding source, Holding target, string itemClass, string? geid, int minute,
        string moveType = "Store", string? result = "succeed") =>
        new(++_id, At(minute), moveType, itemClass, geid, 1, source.Kind, source.Key, target.Kind, target.Key,
            target.Key, result);

    private static MoveRecord Died(string itemClass, string geid) =>
        Move(Body, Corpse, itemClass, geid, minute: 10, moveType: LogIngestor.DeathMove, result: null);

    private static LedgerReplay.WornSighting Worn(int minute, string geid, string itemClass, string port) =>
        new(At(minute), geid, itemClass, port);

    private static LedgerReplay Run(MoveRecord[] moves, LedgerReplay.WornSighting[] worn) =>
        LedgerReplay.Run(moves, worn, carriedContainers: new HashSet<string> { BackpackGeid, ArmorGeid });

    [Fact]
    public void A_backpack_on_the_corpse_takes_its_named_and_counted_contents_with_it()
    {
        var ledger = Run(
            [
                Move(Station, BackpackHolding, "Carryable_1H_SQ_Compboard", "945947121005", minute: 2),
                Move(Station, BackpackHolding, "Drink_bottle_water_old_2_a", geid: null, minute: 3, moveType: "Move"),
                Died(Backpack, BackpackGeid),
            ],
            [Worn(1, BackpackGeid, Backpack, "backpack")]);

        Assert.Equal(Corpse, ledger.InstanceAt[BackpackGeid]);

        // Still in the backpack: the backpack is what moved.
        Assert.Equal(BackpackHolding, ledger.InstanceAt["945947121005"]);
        Assert.Equal(1, ledger.LooseIn(BackpackHolding, "Drink_bottle_water_old_2_a").Sum(g => g.Quantity));
        Assert.Equal(1, ledger.Stats.LeftOnCorpse);
    }

    [Fact]
    public void A_new_loadout_on_the_same_port_does_not_move_the_dead_one_again()
    {
        var ledger = Run(
            [Died(Backpack, BackpackGeid)],
            [
                Worn(1, BackpackGeid, Backpack, "backpack"),
                Worn(11, "946952444689", Backpack, "backpack"),
            ]);

        Assert.Equal(Corpse, ledger.InstanceAt[BackpackGeid]);
        Assert.Equal(Body, ledger.InstanceAt["946952444689"]);
        Assert.Equal(0, ledger.Stats.WornEvicted);
    }

    [Fact]
    public void What_was_on_the_body_as_a_count_goes_onto_the_corpse_too()
    {
        var ledger = Run(
            [
                Move(Station, Body, "crlf_medpen", geid: null, minute: 2, moveType: "Move"),
                Died(Backpack, BackpackGeid),
            ],
            [Worn(1, BackpackGeid, Backpack, "backpack")]);

        Assert.Empty(ledger.LooseIn(Body, "crlf_medpen"));
        Assert.Equal(1, ledger.LooseIn(Corpse, "crlf_medpen").Sum(g => g.Quantity));
    }

    [Fact]
    public void A_magazine_named_only_by_the_corpse_is_the_one_counted_in_the_armour()
    {
        const string magazine = "behr_rifle_ballistic_03_mag";
        var ledger = Run(
            [
                Move(Station, Armor, magazine, geid: null, minute: 2, moveType: "Move"),
                Move(Station, Armor, magazine, geid: null, minute: 3, moveType: "Move"),
                Died("qrt_utility_heavy_core_01_01_03", ArmorGeid),
                Died(magazine, "946063332475"),
            ],
            [Worn(1, ArmorGeid, "qrt_utility_heavy_core_01_01_03", "Armor_Torso")]);

        // Two magazines in all: one now named on the corpse, one still counted in the armour.
        Assert.Equal(Corpse, ledger.InstanceAt["946063332475"]);
        Assert.Equal(1, ledger.LooseIn(Armor, magazine).Sum(g => g.Quantity));
    }

    [Fact]
    public void Looting_an_item_back_off_the_body_takes_it_off_the_corpse()
    {
        var ledger = Run(
            [
                Move(Station, BackpackHolding, "none_rifle_multi_01", "945948376212", minute: 2),
                Died(Backpack, BackpackGeid),
                Move(BackpackHolding, Body, "none_rifle_multi_01", "945948376212", minute: 20, moveType: "Interaction"),
            ],
            [Worn(1, BackpackGeid, Backpack, "backpack")]);

        Assert.Equal(Body, ledger.InstanceAt["945948376212"]);
    }
}
