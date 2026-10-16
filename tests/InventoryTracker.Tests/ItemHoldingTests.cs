using InventoryTracker.Model;
using InventoryTracker.Resolve;
using InventoryTracker.Data;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

public class ItemHoldingTests
{
    private static ItemHolding At(params HoldingLink[] chain) =>
        new("481104483777", "aril_arms_01", 1, chain, DateTimeOffset.UnixEpoch, "Store", 0.9, false, []);

    [Fact]
    public void Stored_directly_at_a_station_is_at_station()
    {
        Assert.True(At(new HoldingLink(InventoryKind.Location, "loc1", "Area18")).IsAtStation);
    }

    [Fact]
    public void Stored_inside_a_container_at_a_station_is_at_station()
    {
        // Chain runs from the immediate holder out: the crate first, the station last.
        var holding = At(
            new HoldingLink(InventoryKind.Container, "crate1", "Crate"),
            new HoldingLink(InventoryKind.Location, "loc1", "Area18"));

        Assert.True(holding.IsAtStation);
    }

    [Theory]
    [InlineData(InventoryKind.Equipped)]
    [InlineData(InventoryKind.Container)]
    [InlineData(InventoryKind.World)]
    public void Anything_not_ending_at_a_station_is_not_at_station(InventoryKind root)
    {
        Assert.False(At(new HoldingLink(root, "k", "label")).IsAtStation);
    }

    [Fact]
    public void Nested_containers_with_no_station_are_not_at_station()
    {
        // The outermost link decides, not the immediate holder.
        var holding = At(
            new HoldingLink(InventoryKind.Container, "pouch1", "Pouch"),
            new HoldingLink(InventoryKind.Container, "backpack1", "Backpack"));

        Assert.False(holding.IsAtStation);
    }

    [Fact]
    public void Unknown_whereabouts_is_not_at_station()
    {
        Assert.False(At().IsAtStation);
    }

    [Fact]
    public void Attachment_only_entity_resolves_individually_and_in_the_full_listing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tracker.db");
        var db = new TrackerDb(path);
        try
        {
            db.Initialize();
            using (var connection = db.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO session(path, first_ts, last_ts, byte_offset, complete)
                    VALUES('attachment-only.log', NULL, NULL, 0, 1);
                    INSERT INTO attachment_sighting(session_id, line_offset, ts, geid, class_name, port, burst_id, placeholder)
                    VALUES(last_insert_rowid(), 1, '2026-07-16T12:00:00.0000000+00:00', 'ATTACH-ONLY', 'helmet_class', 'head', 1, 0);
                    """;
                command.ExecuteNonQuery();
            }

            var resolver = HoldingResolver.Load(db, DateTimeOffset.Parse("2026-07-16T13:00:00Z"));
            var listed = Assert.Single(resolver.ResolveAll());
            var individual = resolver.Resolve("ATTACH-ONLY");

            Assert.NotNull(individual);
            Assert.Equal(listed.Geid, individual.Geid);
            Assert.Equal("helmet_class", individual.ItemClass);
            Assert.Equal(listed.Chain, individual.Chain);
            Assert.Empty(resolver.History("ATTACH-ONLY"));
        }
        finally
        {
            using (var poolKey = db.Open())
            {
                SqliteConnection.ClearPool(poolKey);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_and_resolve_all_follow_a_named_entity_through_class_changes()
    {
        const string geid = "ENTITY-RESOLVER";
        const string firstClass = "Rifle_01";
        const string secondClass = "rifle_02";
        var directory = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new TrackerDb(Path.Combine(directory, "tracker.db"));
        try
        {
            db.Initialize();
            using (var connection = db.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO session(path, first_ts, last_ts, byte_offset, complete)
                    VALUES('identity-resolver.log', NULL, NULL, 0, 1);
                    """;
                command.ExecuteNonQuery();
            }

            AddMove(db, 1, "2026-07-16T12:01:00+00:00", firstClass, geid,
                "Invalid", "", "Location", "station-1");
            AssertResolved(db, geid, firstClass);

            AddMove(db, 2, "2026-07-16T12:02:00+00:00", secondClass, geid,
                "Location", "station-1", "Container", "crate-1");
            AssertResolved(db, geid, secondClass);

            AddMove(db, 3, "2026-07-16T12:03:00+00:00", secondClass, geid,
                "Container", "crate-1", "Location", "station-2");
            AssertResolved(db, geid, secondClass);
        }
        finally
        {
            using (var poolKey = db.Open())
            {
                SqliteConnection.ClearPool(poolKey);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AddMove(
        TrackerDb db, int offset, string timestamp, string itemClass, string geid,
        string sourceKind, string sourceKey, string targetKind, string targetKey)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO move(
                session_id, line_offset, request_no, ts, move_type, item_class, item_geid, amount,
                src_kind, src_key, tgt_raw, tgt_kind, tgt_key, result)
            VALUES(
                (SELECT id FROM session WHERE path = 'identity-resolver.log'),
                $offset, $offset, $timestamp, 'Store', $class, $geid, 1,
                $sourceKind, $sourceKey, $targetRaw, $targetKind, $targetKey, 'succeed');
            """;
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.Parameters.AddWithValue("$class", itemClass);
        command.Parameters.AddWithValue("$geid", geid);
        command.Parameters.AddWithValue("$sourceKind", sourceKind);
        command.Parameters.AddWithValue("$sourceKey", sourceKey);
        command.Parameters.AddWithValue("$targetRaw", targetKind + ":" + targetKey + ":0");
        command.Parameters.AddWithValue("$targetKind", targetKind);
        command.Parameters.AddWithValue("$targetKey", targetKey);
        command.ExecuteNonQuery();
    }

    private static void AssertResolved(TrackerDb db, string geid, string itemClass)
    {
        var resolver = HoldingResolver.Load(db, DateTimeOffset.Parse("2026-07-16T13:00:00Z"));
        var listed = Assert.Single(resolver.ResolveAll(), item => item.Geid == geid);
        var individual = resolver.Resolve(geid);

        Assert.NotNull(individual);
        Assert.Equal(listed.Geid, individual.Geid);
        Assert.Equal(itemClass, individual.ItemClass);
        Assert.Equal(listed.ItemClass, individual.ItemClass);
        Assert.Equal(listed.Chain, individual.Chain);
        Assert.Equal(listed.LastMove, individual.LastMove);
    }

    // A04: the rows the resolver shows for unnamed units must describe every unit they hold, and
    // showing a mixed stack must not lose which of its units a later move may reconcile. Each
    // theory pairs the case that exposes the defect with a control differing in one parameter.

    private const string Magazine = "behr_rifle_ballistic_03_mag";
    private const string ProvenanceLog = "provenance.log";
    private const string SeedCrate = "681562156498";
    private const string StackStation = "2000000001";
    private const string OtherPlace = "2000000002";
    private const string DuplicateCaveat = "we saw this arrive but never saw where it came from";
    private const string UnconfirmedCaveat = "the game never confirmed the move";
    private const string MovedByUpdateCaveat = "assumed moved here by the game update to build 200";

    private static readonly DateTimeOffset ProvenanceAsOf = DateTimeOffset.Parse("2026-07-16T13:00:00Z");

    private static void WithProvenanceDb(Action<TrackerDb> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new TrackerDb(Path.Combine(directory, "tracker.db"));
        try
        {
            db.Initialize();
            test(db);
        }
        finally
        {
            using (var poolKey = db.Open())
            {
                SqliteConnection.ClearPool(poolKey);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AddSession(
        TrackerDb db, string path, int? build = null, string? firstTs = null, string? location = null)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session(path, first_ts, last_ts, byte_offset, complete, build, first_loc_id, cur_loc_id)
            VALUES($path, $first, NULL, 0, 1, $build, $location, $location);
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$first", (object?)firstTs ?? DBNull.Value);
        command.Parameters.AddWithValue("$build", (object?)build ?? DBNull.Value);
        command.Parameters.AddWithValue("$location", (object?)location ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>One move at 12:<paramref name="minute"/>, which also serves as its line offset.</summary>
    private static void AddProvenanceMove(
        TrackerDb db, int minute, string? geid, int amount,
        string sourceKind, string sourceKey, string targetKind, string targetKey, string? result = "succeed")
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO move(
                session_id, line_offset, request_no, ts, move_type, item_class, item_geid, amount,
                src_kind, src_key, tgt_raw, tgt_kind, tgt_key, result)
            VALUES(
                (SELECT id FROM session WHERE path = $session),
                $offset, $offset, $timestamp, 'Move', $class, $geid, $amount,
                $sourceKind, $sourceKey, $targetRaw, $targetKind, $targetKey, $result);
            """;
        command.Parameters.AddWithValue("$session", ProvenanceLog);
        command.Parameters.AddWithValue("$offset", minute);
        command.Parameters.AddWithValue("$timestamp", $"2026-07-16T12:{minute:00}:00+00:00");
        command.Parameters.AddWithValue("$class", Magazine);
        command.Parameters.AddWithValue("$geid", (object?)geid ?? DBNull.Value);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$sourceKind", sourceKind);
        command.Parameters.AddWithValue("$sourceKey", sourceKey);
        command.Parameters.AddWithValue("$targetRaw", targetKind + ":" + targetKey + ":0");
        command.Parameters.AddWithValue("$targetKind", targetKind);
        command.Parameters.AddWithValue("$targetKey", targetKey);
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static List<ItemHolding> RowsAt(TrackerDb db, string locationId) =>
        HoldingResolver.Load(db, ProvenanceAsOf).ResolveAll()
            .Where(h => h.ItemClass == Magazine && h.Root?.Key == locationId)
            .ToList();

    private static int UnnamedUnits(IEnumerable<ItemHolding> rows, string caveat, bool flagged) =>
        rows.Where(h => h.Geid is null
                        && h.Caveats.Any(c => c.StartsWith(caveat, StringComparison.Ordinal)) == flagged)
            .Sum(h => h.Quantity);

    private static string Describe(IEnumerable<ItemHolding> rows) =>
        string.Join(" | ", rows.Select(h =>
            $"{h.Geid ?? "unnamed"} x{h.Quantity} score={h.Score:F3} [{string.Join("; ", h.Caveats)}]"));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_displayed_mixed_stack_still_reconciles_only_its_inferred_unit_after_later_moves(int namedArrivals)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddProvenanceMove(db, 1, null, 10, "Invalid", "", "Container", SeedCrate);
            AddProvenanceMove(db, 2, null, 10, "Container", SeedCrate, "Location", StackStation);
            AddProvenanceMove(db, 3, "G1", 1, "Invalid", "", "Location", OtherPlace);
            AddProvenanceMove(db, 4, "G2", 1, "Invalid", "", "Location", OtherPlace);
            AddProvenanceMove(db, 5, null, 1, "Container", SeedCrate, "Location", StackStation);

            var shown = RowsAt(db, StackStation);
            Assert.Equal(11, shown.Where(h => h.Geid is null).Sum(h => h.Quantity));

            for (var i = 1; i <= namedArrivals; i++)
            {
                AddProvenanceMove(db, 5 + i, $"G{i}", 1, "Location", OtherPlace, "Location", StackStation);
            }

            var after = RowsAt(db, StackStation);
            var unnamed = after.Where(h => h.Geid is null).Sum(h => h.Quantity);
            var named = after.Count(h => h.Geid is not null);
            Assert.True(
                unnamed == 10 && named == namedArrivals,
                $"Expected unnamed=10, named={namedArrivals}; got unnamed={unnamed}, named={named}: {Describe(after)}");
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void The_duplicate_risk_of_a_guess_is_shown_on_that_guess_alone(int countedUnits)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            if (countedUnits > 0)
            {
                AddProvenanceMove(db, 1, null, countedUnits, "Invalid", "", "Container", SeedCrate);
                AddProvenanceMove(db, 2, null, countedUnits, "Container", SeedCrate, "Location", StackStation);
            }

            // Held elsewhere when the unsourced unit arrives: it may be that item, counted twice.
            AddProvenanceMove(db, 3, "G1", 1, "Invalid", "", "Location", OtherPlace);
            AddProvenanceMove(db, 4, null, 1, "Container", SeedCrate, "Location", StackStation);

            var rows = RowsAt(db, StackStation);
            var flagged = UnnamedUnits(rows, DuplicateCaveat, flagged: true);
            var clean = UnnamedUnits(rows, DuplicateCaveat, flagged: false);
            Assert.True(
                flagged == 1 && clean == countedUnits,
                $"Expected 1 unit flagged as a possible duplicate and {countedUnits} clean; got {flagged} and " +
                $"{clean}: {Describe(rows)}");

            // The counted units no longer pay for the guess's doubt.
            var guess = rows.Single(h => h.Caveats.Any(c => c.StartsWith(DuplicateCaveat, StringComparison.Ordinal)));
            Assert.All(
                rows.Where(h => h.Geid is null && !ReferenceEquals(h, guess)),
                counted => Assert.True(counted.Score > guess.Score, Describe(rows)));
        });
    }

    [Theory]
    [InlineData("succeed")]
    [InlineData(null)]
    public void An_unconfirmed_arrival_lowers_only_the_units_it_brought(string? lastResult)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddProvenanceMove(db, 1, null, 11, "Invalid", "", "Container", SeedCrate);
            AddProvenanceMove(db, 2, null, 10, "Container", SeedCrate, "Location", StackStation);
            AddProvenanceMove(db, 3, null, 1, "Container", SeedCrate, "Location", StackStation, lastResult);

            var rows = RowsAt(db, StackStation);
            var expectedUnconfirmed = lastResult is null ? 1 : 0;
            var unconfirmed = UnnamedUnits(rows, UnconfirmedCaveat, flagged: true);
            var confirmed = UnnamedUnits(rows, UnconfirmedCaveat, flagged: false);
            Assert.True(
                unconfirmed == expectedUnconfirmed && confirmed == 11 - expectedUnconfirmed,
                $"Expected {expectedUnconfirmed} unconfirmed and {11 - expectedUnconfirmed} confirmed; got " +
                $"{unconfirmed} and {confirmed}: {Describe(rows)}");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Units_an_update_moved_keep_its_caveat_after_later_arrivals_join_them(bool arrivalAfterUpdate)
    {
        // Build 200 starts at 12:30 with the player spawning at another station than the one they
        // logged out at, so the update is assumed to have moved the three units stored there.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog, build: 100, firstTs: "2026-07-16T12:00:00+00:00", location: OtherPlace);
            AddSession(db, "provenance-2.log", build: 200, firstTs: "2026-07-16T12:30:00+00:00", location: StackStation);
            AddProvenanceMove(db, 1, null, 5, "Invalid", "", "Container", SeedCrate);
            AddProvenanceMove(db, 2, null, 3, "Container", SeedCrate, "Location", OtherPlace);
            if (arrivalAfterUpdate)
            {
                AddProvenanceMove(db, 40, null, 2, "Container", SeedCrate, "Location", StackStation);
            }

            var rows = RowsAt(db, StackStation);
            var moved = UnnamedUnits(rows, MovedByUpdateCaveat, flagged: true);
            var arrived = UnnamedUnits(rows, MovedByUpdateCaveat, flagged: false);
            var expectedArrived = arrivalAfterUpdate ? 2 : 0;
            Assert.True(
                moved == 3 && arrived == expectedArrived,
                $"Expected 3 units moved by the update and {expectedArrived} arrived after it; got {moved} and " +
                $"{arrived}: {Describe(rows)}");
        });
    }

    // L05-R1: each provenance group is dated by its own arrivals, so its age and the updates it
    // crossed are its own. Ten counted units stored before an update are not made younger by a
    // guess that joined them after it; the guess is not made older either. Only units of one
    // provenance share a date — the newest of their arrivals — as the whole stack used to.
    // These tests pin the behaviour delivered by L05 iteration 01 and decided by the coordinator
    // (MANDAT-v3, option a): they characterise it, they were not written ahead of it.

    private const string UpdateCrossingCaveat = "placed before the game update to build 200";

    private const string ResumedUpdateCaveat =
        "placed before the game update to build 200, which no later move has checked — the player picked up " +
        "where they had logged out, so nothing says whether it moved anything";

    private const string FirstSeenCaveat = "first seen being moved out of somewhere we had no record of";

    private static List<ItemHolding> RowsAt(TrackerDb db, string locationId, DateTimeOffset asOf) =>
        HoldingResolver.Load(db, asOf).ResolveAll()
            .Where(h => h.ItemClass == Magazine && h.Root?.Key == locationId)
            .ToList();

    /// <summary>One move at an arbitrary instant, for dates beyond the single hour <see cref="AddProvenanceMove"/> covers.</summary>
    private static void AddProvenanceMoveAt(
        TrackerDb db, int offset, string timestamp, int amount,
        string sourceKind, string sourceKey, string targetKind, string targetKey)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO move(
                session_id, line_offset, request_no, ts, move_type, item_class, item_geid, amount,
                src_kind, src_key, tgt_raw, tgt_kind, tgt_key, result)
            VALUES(
                (SELECT id FROM session WHERE path = $session),
                $offset, $offset, $timestamp, 'Move', $class, NULL, $amount,
                $sourceKind, $sourceKey, $targetRaw, $targetKind, $targetKey, 'succeed');
            """;
        command.Parameters.AddWithValue("$session", ProvenanceLog);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.Parameters.AddWithValue("$class", Magazine);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$sourceKind", sourceKind);
        command.Parameters.AddWithValue("$sourceKey", sourceKey);
        command.Parameters.AddWithValue("$targetRaw", targetKind + ":" + targetKey + ":0");
        command.Parameters.AddWithValue("$targetKind", targetKind);
        command.Parameters.AddWithValue("$targetKey", targetKey);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Build 100 from 12:00, build 200 from 12:30, the player spawning in both at the place they
    /// logged out from: the update is only weighed, it moves nothing.
    /// </summary>
    private static void AddResumedUpdate(TrackerDb db)
    {
        AddSession(db, ProvenanceLog, build: 100, firstTs: "2026-07-16T12:00:00+00:00", location: OtherPlace);
        AddSession(db, "provenance-2.log", build: 200, firstTs: "2026-07-16T12:30:00+00:00", location: OtherPlace);
    }

    private static bool HasCaveat(ItemHolding row, string caveat) =>
        row.Caveats.Any(c => c.StartsWith(caveat, StringComparison.Ordinal));

    [Fact]
    public void Counted_units_stored_before_an_update_still_cross_it_when_a_guess_joins_them_after()
    {
        // Astra's H2 probe (revue-01): ten counted units stored at 12:02, build 200 at 12:30, one
        // guess at 12:40. Before L05 the guess re-dated all eleven to 12:40: x11, 0.700, High, no
        // update caveat. Each group now keeps its own date.
        WithProvenanceDb(db =>
        {
            AddResumedUpdate(db);
            AddProvenanceMove(db, 1, null, 10, "Invalid", "", "Container", SeedCrate);
            AddProvenanceMove(db, 2, null, 10, "Container", SeedCrate, "Location", StackStation);
            AddProvenanceMove(db, 40, null, 1, "Location", OtherPlace, "Location", StackStation);

            var rows = RowsAt(db, StackStation, DateTimeOffset.Parse("2026-07-17T13:00:00Z"));

            Assert.True(rows.Count == 2 && rows.All(h => h.Geid is null), Describe(rows));
            var guess = Assert.Single(rows, h => h.Quantity == 1);
            var counted = Assert.Single(rows, h => h.Quantity == 10);

            Assert.Equal(DateTimeOffset.Parse("2026-07-16T12:40:00Z"), guess.LastMove);
            Assert.Equal(0.700, guess.Score, 3);
            Assert.Equal(Confidence.High, guess.Confidence);
            Assert.True(HasCaveat(guess, FirstSeenCaveat), Describe(rows));
            Assert.False(HasCaveat(guess, UpdateCrossingCaveat), Describe(rows));

            Assert.Equal(DateTimeOffset.Parse("2026-07-16T12:02:00Z"), counted.LastMove);
            Assert.Equal(0.350, counted.Score, 3);
            Assert.Equal(Confidence.Low, counted.Confidence);
            // The whole caveat is compared, not its opening words: all four wordings of an update
            // caveat begin "placed before the game update to build 200", and only this one says the
            // player resumed where they had logged out, so nothing says the update moved anything.
            // A rewording breaks this line without a change of behaviour; the structured twin,
            // Stored_units_cross_a_resumed_unverified_update_by_their_own_groups_date (L05-R10),
            // pins the same branch through Updates, UpdateSurvivals and MovedThePlayer instead.
            Assert.Contains(ResumedUpdateCaveat, counted.Caveats);
            Assert.False(HasCaveat(counted, FirstSeenCaveat), Describe(rows));
        });
    }

    [Fact]
    public void Counted_units_keep_their_own_age_when_a_recent_guess_joins_them()
    {
        // Astra's H1 probe (revue-01): ten counted units stored on 2026-07-01, one guess on
        // 2026-08-25, read on 2026-09-01. Before L05 all eleven were last seen on 08-25.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddProvenanceMoveAt(db, 1, "2026-07-01T12:00:00+00:00", 10, "Invalid", "", "Container", SeedCrate);
            AddProvenanceMoveAt(db, 2, "2026-07-01T12:01:00+00:00", 10, "Container", SeedCrate, "Location", StackStation);
            AddProvenanceMoveAt(db, 3, "2026-08-25T12:00:00+00:00", 1, "Location", OtherPlace, "Location", StackStation);

            var rows = RowsAt(db, StackStation, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

            Assert.True(rows.Count == 2 && rows.All(h => h.Geid is null), Describe(rows));
            var guess = Assert.Single(rows, h => h.Quantity == 1);
            var counted = Assert.Single(rows, h => h.Quantity == 10);

            Assert.Equal(DateTimeOffset.Parse("2026-08-25T12:00:00Z"), guess.LastMove);
            Assert.DoesNotContain(guess.Caveats, c => c.StartsWith("last seen", StringComparison.Ordinal));
            Assert.Equal(DateTimeOffset.Parse("2026-07-01T12:01:00Z"), counted.LastMove);
            Assert.Contains("last seen 62 days ago", counted.Caveats);

            // Age past 30 days is mentioned, not scored: both rows keep 0.700 (the unnamed station).
            Assert.Equal(0.700, guess.Score, 3);
            Assert.Equal(0.700, counted.Score, 3);
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Only_a_later_arrival_of_another_provenance_leaves_stored_units_dated_before_an_update(int seededUnits)
    {
        // The same three moves; only what the crate held differs. With ten, the 12:40 move finds
        // the crate empty and its unit is a guess: a group of its own, so the ten stored at 12:02
        // still cross the update. With eleven, the 12:40 unit is counted like the ten: one group,
        // dated by its newest arrival, which no longer crosses the update — as before L05.
        WithProvenanceDb(db =>
        {
            AddResumedUpdate(db);
            AddProvenanceMove(db, 1, null, seededUnits, "Invalid", "", "Container", SeedCrate);
            AddProvenanceMove(db, 2, null, 10, "Container", SeedCrate, "Location", StackStation);
            AddProvenanceMove(db, 40, null, 1, "Container", SeedCrate, "Location", StackStation);

            var rows = RowsAt(db, StackStation);
            var crossing = UnnamedUnits(rows, UpdateCrossingCaveat, flagged: true);
            var notCrossing = UnnamedUnits(rows, UpdateCrossingCaveat, flagged: false);
            var guessed = seededUnits == 10;
            Assert.True(
                crossing == (guessed ? 10 : 0) && notCrossing == (guessed ? 1 : 11) && rows.Count == (guessed ? 2 : 1),
                $"Expected {(guessed ? "x10 crossing the update and x1 not" : "one x11 row not crossing it")}; got " +
                $"{crossing} crossing, {notCrossing} not: {Describe(rows)}");

            var newest = Assert.Single(rows, h => !HasCaveat(h, UpdateCrossingCaveat));
            Assert.Equal(DateTimeOffset.Parse("2026-07-16T12:40:00Z"), newest.LastMove);
            Assert.Equal(0.700, newest.Score, 3);
        });
    }

    // L05-R7 (iteration 03): the other uses of a group's own date in HoldingResolver. Like the
    // L05-R1 tests above, these characterise behaviour delivered by L05 and accepted on
    // iteration 02: they were written after it and pass on it. Each pairs a late guess joining
    // counted units (a group of its own, dated by its own arrival) with a control differing in
    // one parameter: the source held enough to count the late unit too, so there is one group,
    // dated by its newest arrival, as the whole stack was dated before L05.

    private const string Ration = "Food_l05_ration";

    /// <summary>A box nobody ever saw placed: not a station, so a ration in it can be doubted.</summary>
    private const string RationBox = "681562156777";

    private const string UsedUpCaveat = "food, drink and medical items are used up without a log line";
    private const string LastSeenOnCharacterCaveat = "last seen on your character ";

    /// <summary>A class-level move of any class at an arbitrary instant.</summary>
    private static void AddClassMoveAt(
        TrackerDb db, int offset, string timestamp, string itemClass, int amount,
        string sourceKind, string sourceKey, string targetKind, string targetKey)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO move(
                session_id, line_offset, request_no, ts, move_type, item_class, item_geid, amount,
                src_kind, src_key, tgt_raw, tgt_kind, tgt_key, result)
            VALUES(
                (SELECT id FROM session WHERE path = $session),
                $offset, $offset, $timestamp, 'Move', $class, NULL, $amount,
                $sourceKind, $sourceKey, $targetRaw, $targetKind, $targetKey, 'succeed');
            """;
        command.Parameters.AddWithValue("$session", ProvenanceLog);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.Parameters.AddWithValue("$class", itemClass);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$sourceKind", sourceKind);
        command.Parameters.AddWithValue("$sourceKey", sourceKey);
        command.Parameters.AddWithValue("$targetRaw", targetKind + ":" + targetKey + ":0");
        command.Parameters.AddWithValue("$targetKind", targetKind);
        command.Parameters.AddWithValue("$targetKey", targetKey);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A complete listing of the player's body at <paramref name="timestamp"/>: the body entity's
    /// own line, and nothing else, so every class the ledger had on the player is left out.
    /// </summary>
    private static void AddBodyListing(TrackerDb db, int offset, string timestamp)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO attachment_sighting(session_id, line_offset, ts, geid, class_name, port, burst_id, placeholder)
            VALUES((SELECT id FROM session WHERE path = $session), $offset, $timestamp, 'BODY-L05', 'body_l05', 'Body_ItemPort', 1, 0);
            """;
        command.Parameters.AddWithValue("$session", ProvenanceLog);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.ExecuteNonQuery();
    }

    private static List<ItemHolding> UnnamedRows(
        TrackerDb db, string itemClass, DateTimeOffset asOf, Func<ItemHolding, bool> where) =>
        HoldingResolver.Load(db, asOf).ResolveAll()
            .Where(h => h.ItemClass == itemClass && h.Geid is null && where(h))
            .ToList();

    private static string DescribeDated(IEnumerable<ItemHolding> rows) =>
        string.Join(" | ", rows.Select(h =>
            $"{h.Geid ?? "unnamed"} x{h.Quantity} score={h.Score:F3} {h.Confidence} last={h.LastMove:yyyy-MM-dd HH:mm} " +
            $"[{string.Join("; ", h.Caveats)}]"));

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Food_put_away_outside_a_station_is_doubted_by_the_age_of_its_own_group(int seededUnits)
    {
        // Astra's K1 probe (revue-02): ten rations put in a box nobody saw placed on 2026-08-01,
        // one more on 2026-08-30, read on 2026-09-01. A consumable untouched for more than a week
        // outside a station is doubted (x0.5). Before L05 the late unit re-dated all eleven to
        // 08-30: x11, 0.600, Medium, not doubted. With ten in the crate the late unit is a guess,
        // a group of its own: the ten are 31 days old and doubted, the guess is two days old.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddClassMoveAt(db, 1, "2026-08-01T11:00:00+00:00", Ration, seededUnits, "Invalid", "", "Container", SeedCrate);
            AddClassMoveAt(db, 2, "2026-08-01T12:00:00+00:00", Ration, 10, "Container", SeedCrate, "Container", RationBox);
            AddClassMoveAt(db, 3, "2026-08-30T12:00:00+00:00", Ration, 1, "Container", SeedCrate, "Container", RationBox);

            var rows = UnnamedRows(db, Ration, DateTimeOffset.Parse("2026-09-01T12:00:00Z"), h => h.Root?.Key == RationBox);
            var guessed = seededUnits == 10;
            Assert.True(rows.Count == (guessed ? 2 : 1), DescribeDated(rows));

            // The newest group: x1 when it is a guess, all eleven otherwise. 0.600 is the box nobody
            // saw placed (x0.6); it is not doubted, being two days old.
            var newest = Assert.Single(rows, h => h.LastMove == DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
            Assert.Equal(guessed ? 1 : 11, newest.Quantity);
            Assert.Equal(0.600, newest.Score, 3);
            Assert.Equal(Confidence.Medium, newest.Confidence);
            Assert.False(HasCaveat(newest, UsedUpCaveat), DescribeDated(rows));

            if (guessed)
            {
                var stored = Assert.Single(rows, h => h.Quantity == 10);
                Assert.Equal(DateTimeOffset.Parse("2026-08-01T12:00:00Z"), stored.LastMove);
                Assert.Equal(0.300, stored.Score, 3);
                Assert.Equal(Confidence.Low, stored.Confidence);
                Assert.Contains(stored.Caveats, c =>
                    c.StartsWith(UsedUpCaveat, StringComparison.Ordinal)
                    && c.Contains("has not moved in 31 days", StringComparison.Ordinal));
            }
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Units_a_listing_left_out_are_last_seen_on_the_character_on_their_own_groups_date(int seededUnits)
    {
        // Ten magazines taken onto the character on 2026-07-16, one more on 2026-07-18, then a
        // listing of the body on 2026-07-20 that names no magazine: all eleven go to LostTrack,
        // and BuildChain says when they were last seen on the character (x0.3). Before L05 the
        // whole stack said 07-18. With ten in the crate the 07-18 unit is a guess, a group of its
        // own, and the ten keep their own date, 07-16.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddClassMoveAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, seededUnits, "Invalid", "", "Container", SeedCrate);
            AddClassMoveAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Equipped", "");
            AddClassMoveAt(db, 3, "2026-07-18T12:00:00+00:00", Magazine, 1, "Container", SeedCrate, "Equipped", "");
            AddBodyListing(db, 4, "2026-07-20T12:00:00+00:00");

            var rows = UnnamedRows(
                db, Magazine, DateTimeOffset.Parse("2026-07-21T12:00:00Z"), h => h.Root?.Kind == InventoryKind.LostTrack);
            var guessed = seededUnits == 10;
            Assert.True(rows.Count == (guessed ? 2 : 1), DescribeDated(rows));
            Assert.All(rows, h => Assert.Equal(0.300, h.Score, 3));

            var newest = Assert.Single(rows, h => h.LastMove == DateTimeOffset.Parse("2026-07-18T12:00:00Z"));
            Assert.Equal(guessed ? 1 : 11, newest.Quantity);
            Assert.True(HasCaveat(newest, LastSeenOnCharacterCaveat + "2026-07-18;"), DescribeDated(rows));

            if (guessed)
            {
                var stored = Assert.Single(rows, h => h.Quantity == 10);
                Assert.Equal(DateTimeOffset.Parse("2026-07-16T12:02:00Z"), stored.LastMove);
                Assert.True(HasCaveat(stored, LastSeenOnCharacterCaveat + "2026-07-16;"), DescribeDated(rows));
            }
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void A_guess_joining_counted_units_dates_only_its_own_row(int seededUnits)
    {
        // No update, no age worth mentioning, nothing consumable: a row's date shows only as its
        // LastMove and in the order ResolveAll lists rows in, newest first. Before L05 the whole
        // stack was dated 12:40. The newest date among the rows, which the station view shows for
        // a pooled class (Browse.razor takes the maximum), is 12:40 either way.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddProvenanceMove(db, 1, null, seededUnits, "Invalid", "", "Container", SeedCrate);
            AddProvenanceMove(db, 2, null, 10, "Container", SeedCrate, "Location", StackStation);
            AddProvenanceMove(db, 40, null, 1, "Container", SeedCrate, "Location", StackStation);

            var all = HoldingResolver.Load(db, ProvenanceAsOf).ResolveAll().ToList();
            var rows = all.Where(h => h.ItemClass == Magazine && h.Root?.Key == StackStation).ToList();
            var guessed = seededUnits == 10;
            Assert.True(rows.Count == (guessed ? 2 : 1) && rows.All(h => h.Geid is null), DescribeDated(rows));

            var latest = DateTimeOffset.Parse("2026-07-16T12:40:00Z");
            Assert.Equal(latest, rows.Max(h => h.LastMove));
            Assert.All(rows, h => Assert.Equal(0.700, h.Score, 3));
            var newest = Assert.Single(rows, h => h.LastMove == latest);
            Assert.Equal(guessed ? 1 : 11, newest.Quantity);

            if (guessed)
            {
                var stored = Assert.Single(rows, h => h.Quantity == 10);
                Assert.Equal(DateTimeOffset.Parse("2026-07-16T12:02:00Z"), stored.LastMove);
                Assert.True(all.IndexOf(newest) < all.IndexOf(stored), DescribeDated(rows));
            }
        });
    }

    // L05-R10 (iteration 03): the structured twin of the whole-text caveat comparison in
    // Counted_units_stored_before_an_update_still_cross_it_when_a_guess_joins_them_after. Which
    // wording an update caveat takes follows from the update's record (verified or not) and from
    // whether it moved the player; both are public, so the branch is checked without the text.
    // A characterisation test like those above, paired with the same one-parameter control.

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Stored_units_cross_a_resumed_unverified_update_by_their_own_groups_date(int seededUnits)
    {
        WithProvenanceDb(db =>
        {
            AddResumedUpdate(db);
            AddProvenanceMove(db, 1, null, seededUnits, "Invalid", "", "Container", SeedCrate);
            AddProvenanceMove(db, 2, null, 10, "Container", SeedCrate, "Location", StackStation);
            AddProvenanceMove(db, 40, null, 1, "Container", SeedCrate, "Location", StackStation);

            var resolver = HoldingResolver.Load(db, DateTimeOffset.Parse("2026-07-17T13:00:00Z"));
            var update = Assert.Single(resolver.Updates);
            Assert.Equal(200, update.Build);
            Assert.Equal(OtherPlace, update.Spawn);
            Assert.Equal(OtherPlace, update.LeftFrom);
            Assert.False(resolver.MovedThePlayer(update));
            var record = resolver.UpdateSurvivals[200];
            Assert.Equal((0, 0, false), (record.Checked, record.Survived, record.Verified));
            Assert.Equal(0.5, record.Factor, 3);

            var rows = resolver.ResolveAll().Where(h => h.ItemClass == Magazine && h.Root?.Key == StackStation).ToList();
            var crossing = rows.Where(h => h.LastMove < update.At).ToList();
            var notCrossing = rows.Where(h => h.LastMove >= update.At).ToList();
            var guessed = seededUnits == 10;
            Assert.True(
                crossing.Sum(h => h.Quantity) == (guessed ? 10 : 0) && notCrossing.Sum(h => h.Quantity) == (guessed ? 1 : 11)
                && rows.Count == (guessed ? 2 : 1),
                DescribeDated(rows));

            Assert.All(crossing, h => Assert.Equal(0.700 * record.Factor, h.Score, 3));
            Assert.All(crossing, h => Assert.Single(h.Caveats, c => c.Contains("build 200", StringComparison.Ordinal)));
            Assert.All(notCrossing, h => Assert.Equal(0.700, h.Score, 3));
            Assert.All(notCrossing, h => Assert.DoesNotContain(h.Caveats, c => c.Contains("build 200", StringComparison.Ordinal)));
        });
    }

    // L05 iteration 04 (D07, L05-R11): every parameter a provenance group hands to the resolver,
    // not only its date. HoldingResolver.Build reads, per group, how its newest unit arrived
    // (ArrivedBy: Carry x0.5, Drop x0.3, the consumable rule's exemption, LastMoveType), whether
    // every move was confirmed (x0.5), whether a unit was a guess (a caveat) that may be counted
    // twice (x0.8), and which update moved it (its caveat, and which crossed update speaks for
    // it). Before L05 one stack carried these for all its units. These tests characterise the
    // behaviour accepted on iteration 03: they were written after it and pass on it. Each pairs a
    // case where the late arrival forms a group of its own with a control differing in one
    // parameter, where all units share one provenance and one row, as the whole stack did.
    // Expected rows are spelled as Characterise writes them, so that a red shows the rows it got.

    /// <summary>Caveats by their opening words, so a row reads in a few tags rather than whole sentences.</summary>
    private static readonly (string Prefix, string Tag)[] CaveatTags =
    [
        ("first seen being moved out of somewhere we had no record of", "FirstSeen"),
        ("we saw this arrive but never saw where it came from", "Duplicate"),
        ("the game never confirmed the move that put it here", "Unconfirmed"),
        ("last picked up in hand to use", "Carried"),
        ("dropped on the floor", "Dropped"),
        ("food, drink and medical items are used up without a log line, and this one has not moved in 31 days", "UsedUp31"),
        ("assumed moved here by the game update to build 200", "MovedBy200"),
        ("assumed moved here by the game update to build 300", "MovedBy300"),
        ("placed before the game update to build 200 — updates have been seen to move stored items", "Crossed200Spawned"),
        ("placed before the game update to build 200, which no later move has checked — the player picked up", "Crossed200Resumed"),
        ("last seen 31 days ago", "Age31"),
        ("this station's id was never paired with a name", "UnnamedStation"),
    ];

    /// <summary>
    /// Each row as quantity, score, confidence, LastMoveType, LastMove and its caveats in order, as
    /// tags; a caveat with no tag is written out whole.
    /// </summary>
    private static string Characterise(IEnumerable<ItemHolding> rows) =>
        string.Join(" | ", rows.Select(h => FormattableString.Invariant(
            $"x{h.Quantity} {h.Score:F3} {h.Confidence} {h.LastMoveType} {h.LastMove:yyyy-MM-dd HH:mm} [") +
            string.Join(", ", h.Caveats.Select(c =>
                CaveatTags.Where(t => c.StartsWith(t.Prefix, StringComparison.Ordinal)).Select(t => t.Tag).FirstOrDefault() ?? c)) +
            "]"));

    /// <summary>One move with its type, action and result, at an arbitrary instant.</summary>
    private static void AddArrivalAt(
        TrackerDb db, int offset, string timestamp, string itemClass, int amount,
        string sourceKind, string sourceKey, string targetKind, string targetKey,
        string moveType = "Move", string? action = null, string? result = "succeed", string? geid = null)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO move(
                session_id, line_offset, request_no, ts, move_type, item_class, item_geid, amount,
                src_kind, src_key, tgt_raw, tgt_kind, tgt_key, result, action)
            VALUES(
                (SELECT id FROM session WHERE path = $session),
                $offset, $offset, $timestamp, $moveType, $class, $geid, $amount,
                $sourceKind, $sourceKey, $targetRaw, $targetKind, $targetKey, $result, $action);
            """;
        command.Parameters.AddWithValue("$session", ProvenanceLog);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.Parameters.AddWithValue("$moveType", moveType);
        command.Parameters.AddWithValue("$class", itemClass);
        command.Parameters.AddWithValue("$geid", (object?)geid ?? DBNull.Value);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$sourceKind", sourceKind);
        command.Parameters.AddWithValue("$sourceKey", sourceKey);
        command.Parameters.AddWithValue("$targetRaw", targetKind + ":" + targetKey + ":0");
        command.Parameters.AddWithValue("$targetKind", targetKind);
        command.Parameters.AddWithValue("$targetKey", targetKey);
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", (object?)action ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Ten magazines counted into <paramref name="targetKind"/>:<see cref="StackStation"/> at 12:02,
    /// then one more out of the same crate at 12:40, by <paramref name="lateMoveType"/> and
    /// <paramref name="lateAction"/>. With ten in the crate the late unit is a guess.
    /// </summary>
    private static List<ItemHolding> LateArrivalRows(
        TrackerDb db, int seededUnits, string targetKind, string lateMoveType = "Move", string? lateAction = null)
    {
        AddSession(db, ProvenanceLog);
        AddArrivalAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, seededUnits, "Invalid", "", "Container", SeedCrate);
        AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, targetKind, StackStation);
        AddArrivalAt(db, 3, "2026-07-16T12:40:00+00:00", Magazine, 1, "Container", SeedCrate, targetKind, StackStation,
            lateMoveType, lateAction);
        return UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void A_unit_carried_in_by_hand_doubts_only_its_own_group(int seededUnits)
    {
        // Astra's P1 probe (revue-03, L05-R11): HoldingResolver.cs:298 and :365. Before L05 the
        // Carry of the late unit stood for all eleven: x11 0.350 Low Carry, "last picked up in
        // hand" on every unit. The ten that came by Move are not doubted any more.
        // The 11-unit control has one provenance by the resolver's flags; D03: its last arrival
        // supplies ArrivedBy to that group.
        WithProvenanceDb(db =>
        {
            var rows = LateArrivalRows(db, seededUnits, "Location", lateAction: "Carry");

            Assert.Equal(
                seededUnits == 10
                    ? "x1 0.350 Low Carry 2026-07-16 12:40 [FirstSeen, Carried, UnnamedStation] | " +
                      "x10 0.700 High Move 2026-07-16 12:02 [UnnamedStation]"
                    : "x11 0.350 Low Carry 2026-07-16 12:40 [Carried, UnnamedStation]",
                Characterise(rows));
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void A_unit_dropped_last_marks_only_its_own_group_as_dropped(int seededUnits)
    {
        // HoldingResolver.cs:320 and :365. Ten magazines put into the world by a Move, one more
        // dropped there. Before L05 the Drop stood for all eleven: x11 0.300 Low, "dropped on the
        // floor" on every unit. A place in the world costs nothing of itself, so the ten score 1.000.
        // The 11-unit control has one provenance by the resolver's flags; D03: its last arrival
        // supplies ArrivedBy to that group.
        WithProvenanceDb(db =>
        {
            var rows = LateArrivalRows(db, seededUnits, "World", lateMoveType: "Drop");

            Assert.Equal(
                seededUnits == 10
                    ? "x1 0.300 Low Drop 2026-07-16 12:40 [FirstSeen, Dropped] | x10 1.000 High Move 2026-07-16 12:02 []"
                    : "x11 0.300 Low Drop 2026-07-16 12:40 [Dropped]",
                Characterise(rows));
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Food_carried_last_exempts_only_its_own_group_from_the_shelf_life_doubt(int seededUnits)
    {
        // HoldingResolver.cs:312 (and :298, :365). Ten rations taken onto the character by a Move
        // on 2026-08-01 at 12:00, one more carried in hand at 13:00; read 31 days later. Both
        // groups are past the week a consumable is trusted, so their dates decide nothing here:
        // only how each arrived. Carried in hand is doubted as such (x0.5) and exempt from the
        // shelf-life rule. Before L05 the Carry stood for all eleven: x11 0.500 Medium Carry,
        // "last picked up in hand", no shelf-life doubt. The ten are now doubted by their shelf
        // life instead (x0.5): the same score, for the reason that applies to them.
        // The 11-unit control has one provenance by the resolver's flags; D03: its last arrival
        // supplies ArrivedBy to that group.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-08-01T11:00:00+00:00", Ration, seededUnits, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-08-01T12:00:00+00:00", Ration, 10, "Container", SeedCrate, "Equipped", "");
            AddArrivalAt(db, 3, "2026-08-01T13:00:00+00:00", Ration, 1, "Container", SeedCrate, "Equipped", "",
                action: "Carry");

            var rows = UnnamedRows(
                db, Ration, DateTimeOffset.Parse("2026-09-01T12:00:00Z"), h => h.Root?.Kind == InventoryKind.Equipped);

            Assert.Equal(
                seededUnits == 10
                    ? "x1 0.500 Medium Carry 2026-08-01 13:00 [FirstSeen, Carried, Age31] | " +
                      "x10 0.500 Medium Move 2026-08-01 12:00 [UsedUp31, Age31]"
                    : "x11 0.500 Medium Carry 2026-08-01 13:00 [Carried, Age31]",
                Characterise(rows));
        });
    }

    [Theory]
    [InlineData("succeed")]
    [InlineData(null)]
    public void An_unconfirmed_last_arrival_costs_only_its_own_row(string? lastResult)
    {
        // HoldingResolver.cs:275. Eleven units counted out of the crate; the last one's move was
        // never confirmed. Before L05 the stack was confirmed only if every move was: x11 0.350
        // Low, "never confirmed" on every unit. The control confirms the last move too.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, 11, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Location", StackStation);
            AddArrivalAt(db, 3, "2026-07-16T12:03:00+00:00", Magazine, 1, "Container", SeedCrate, "Location", StackStation,
                result: lastResult);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);

            Assert.Equal(
                lastResult is null
                    ? "x1 0.350 Low Move 2026-07-16 12:03 [Unconfirmed, UnnamedStation] | " +
                      "x10 0.700 High Move 2026-07-16 12:02 [UnnamedStation]"
                    : "x11 0.700 High Move 2026-07-16 12:03 [UnnamedStation]",
                Characterise(rows));
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Only_the_guess_says_it_was_first_seen_somewhere_unrecorded(int seededUnits)
    {
        // HoldingResolver.cs:290. A guess of a class held nowhere else costs nothing, but says so.
        // Before L05 the stack was inferred if any unit was: x11 0.700 High, "first seen" on every
        // unit. Only the scores agree.
        WithProvenanceDb(db =>
        {
            var rows = LateArrivalRows(db, seededUnits, "Location");

            Assert.Equal(
                seededUnits == 10
                    ? "x1 0.700 High Move 2026-07-16 12:40 [FirstSeen, UnnamedStation] | " +
                      "x10 0.700 High Move 2026-07-16 12:02 [UnnamedStation]"
                    : "x11 0.700 High Move 2026-07-16 12:40 [UnnamedStation]",
                Characterise(rows));
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Only_a_guess_that_may_be_counted_twice_pays_for_it(int seededUnits)
    {
        // HoldingResolver.cs:285. G1 is held at another station when the unsourced unit arrives,
        // so the guess may be G1 counted twice (x0.8). Before L05 the stack carried the risk if
        // any unit did: x11 0.560 Medium, the duplicate caveat on every unit. Exact values of the
        // case The_duplicate_risk_of_a_guess_is_shown_on_that_guess_alone counts.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, seededUnits, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Location", StackStation);
            AddArrivalAt(db, 3, "2026-07-16T12:03:00+00:00", Magazine, 1, "Invalid", "", "Location", OtherPlace, geid: "G1");
            AddArrivalAt(db, 4, "2026-07-16T12:04:00+00:00", Magazine, 1, "Container", SeedCrate, "Location", StackStation);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);

            Assert.Equal(
                seededUnits == 10
                    ? "x1 0.560 Medium Move 2026-07-16 12:04 [Duplicate, UnnamedStation] | " +
                      "x10 0.700 High Move 2026-07-16 12:02 [UnnamedStation]"
                    : "x11 0.700 High Move 2026-07-16 12:04 [UnnamedStation]",
                Characterise(rows));
        });
    }

    /// <summary>
    /// Thirteen magazines counted out of the crate: ten stored at 12:02 at the station the player
    /// spawns at after the update, or where they logged out before it; three at 12:03 where they
    /// logged out. The update moves what is stored where they logged out to where they spawn.
    /// </summary>
    private static void StoreAroundAnUpdate(TrackerDb db, bool tenWhereThePlayerSpawns)
    {
        AddArrivalAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, 13, "Invalid", "", "Container", SeedCrate);
        AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Location",
            tenWhereThePlayerSpawns ? StackStation : OtherPlace);
        AddArrivalAt(db, 3, "2026-07-16T12:03:00+00:00", Magazine, 3, "Container", SeedCrate, "Location", OtherPlace);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Units_already_where_the_player_spawned_are_not_said_to_be_moved_by_the_update(bool tenWhereThePlayerSpawns)
    {
        // HoldingResolver.cs:348 and :349. Build 200 at 12:30 sends the player from OtherPlace to
        // StackStation. Before L05 the merged stack took the update's build for all thirteen:
        // x13 0.350 Low "assumed moved here by the game update to build 200". The ten that were
        // already there now say they were placed before an update that moves what is stored; the
        // score is the same (x0.5, the update is unverified). In the control all thirteen moved.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog, build: 100, firstTs: "2026-07-16T12:00:00+00:00", location: OtherPlace);
            AddSession(db, "provenance-2.log", build: 200, firstTs: "2026-07-16T12:30:00+00:00", location: StackStation);
            StoreAroundAnUpdate(db, tenWhereThePlayerSpawns);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);

            Assert.Equal(
                tenWhereThePlayerSpawns
                    ? "x3 0.350 Low Move 2026-07-16 12:03 [MovedBy200, UnnamedStation] | " +
                      "x10 0.350 Low Move 2026-07-16 12:02 [Crossed200Spawned, UnnamedStation]"
                    : "x13 0.350 Low Move 2026-07-16 12:03 [MovedBy200, UnnamedStation]",
                Characterise(rows));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Of_two_updates_crossed_alike_each_group_is_described_by_its_own(bool tenWhereThePlayerSpawns)
    {
        // HoldingResolver.cs:344 (and :348, :349). Build 200 at 12:30 resumes where the player
        // logged out; build 300 at 12:50 sends them from OtherPlace to StackStation. Both updates
        // are unverified (x0.5), so on a tie the update that moved a group speaks for it. Before
        // L05 the merged stack took build 300 for all thirteen: x13 0.350 Low "assumed moved here
        // by the game update to build 300". The ten that were already there were moved by
        // neither: the first update they crossed, the resumed one, describes them.
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog, build: 100, firstTs: "2026-07-16T12:00:00+00:00", location: OtherPlace);
            AddSession(db, "provenance-2.log", build: 200, firstTs: "2026-07-16T12:30:00+00:00", location: OtherPlace);
            AddSession(db, "provenance-3.log", build: 300, firstTs: "2026-07-16T12:50:00+00:00", location: StackStation);
            StoreAroundAnUpdate(db, tenWhereThePlayerSpawns);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);

            Assert.Equal(
                tenWhereThePlayerSpawns
                    ? "x3 0.350 Low Move 2026-07-16 12:03 [MovedBy300, UnnamedStation] | " +
                      "x10 0.350 Low Move 2026-07-16 12:02 [Crossed200Resumed, UnnamedStation]"
                    : "x13 0.350 Low Move 2026-07-16 12:03 [MovedBy300, UnnamedStation]",
                Characterise(rows));
        });
    }

    // L05 iteration 05: group-backed output changes when withdrawal removes the only unit that
    // carried an indicator. Each true row is paired with its no-withdrawal control (R16).

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Withdrawing_the_only_guess_clears_first_seen_from_the_remaining_group(bool withdraw)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, 10, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Location", StackStation);
            AddArrivalAt(db, 3, "2026-07-16T12:40:00+00:00", Magazine, 1, "Container", SeedCrate, "Location", StackStation);
            if (withdraw)
                AddArrivalAt(db, 4, "2026-07-16T12:50:00+00:00", Magazine, 1, "Location", StackStation, "Container", SeedCrate);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);
            if (withdraw)
                Assert.Equal("x10 0.700 High Move 2026-07-16 12:02 [UnnamedStation]", Characterise(rows));
            else
            {
                Assert.Equal(11, rows.Sum(h => h.Quantity));
                Assert.Contains(rows, h => h.Caveats.Any(c => c.StartsWith(
                    "first seen being moved out of somewhere we had no record of", StringComparison.Ordinal)));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Withdrawing_the_only_unconfirmed_unit_clears_its_caveat_from_the_remaining_group(bool withdraw)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, 11, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Location", StackStation);
            AddArrivalAt(db, 3, "2026-07-16T12:03:00+00:00", Magazine, 1, "Container", SeedCrate, "Location", StackStation, result: null);
            if (withdraw)
                AddArrivalAt(db, 4, "2026-07-16T12:50:00+00:00", Magazine, 1, "Location", StackStation, "Container", SeedCrate);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);
            if (withdraw)
                Assert.Equal("x10 0.700 High Move 2026-07-16 12:02 [UnnamedStation]", Characterise(rows));
            else
            {
                Assert.Equal(11, rows.Sum(h => h.Quantity));
                Assert.Contains(rows, h => h.Caveats.Any(c => c.StartsWith(
                    "the game never confirmed the move that put it here", StringComparison.Ordinal)));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Withdrawing_the_only_duplicate_risk_unit_clears_its_caveat_from_the_remaining_group(bool withdraw)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, 10, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Location", StackStation);
            AddArrivalAt(db, 3, "2026-07-16T12:03:00+00:00", Magazine, 1, "Invalid", "", "Location", OtherPlace, geid: "G1");
            AddArrivalAt(db, 4, "2026-07-16T12:04:00+00:00", Magazine, 1, "Container", SeedCrate, "Location", StackStation);
            if (withdraw)
                AddArrivalAt(db, 5, "2026-07-16T12:50:00+00:00", Magazine, 1, "Location", StackStation, "Container", SeedCrate);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);
            if (withdraw)
                Assert.Equal("x10 0.700 High Move 2026-07-16 12:02 [UnnamedStation]", Characterise(rows));
            else
            {
                Assert.Equal(11, rows.Sum(h => h.Quantity));
                Assert.Contains(rows, h => h.Caveats.Any(c => c.StartsWith(
                    "we saw this arrive but never saw where it came from", StringComparison.Ordinal)));
            }
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Food_dropped_last_exempts_only_its_own_group_from_the_shelf_life_doubt(int seededUnits)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-08-01T11:00:00+00:00", Ration, seededUnits, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-08-01T12:00:00+00:00", Ration, 10, "Container", SeedCrate, "World", StackStation);
            AddArrivalAt(db, 3, "2026-08-01T13:00:00+00:00", Ration, 1, "Container", SeedCrate, "World", StackStation, moveType: "Drop");

            var asOf = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
            var rows = UnnamedRows(db, Ration, asOf, h => h.Root?.Key == StackStation);
            Assert.Equal(
                seededUnits == 10
                    ? "x1 0.300 Low Drop 2026-08-01 13:00 [FirstSeen, Dropped, Age31] | " +
                      "x10 0.500 Medium Move 2026-08-01 12:00 [UsedUp31, Age31]"
                    : "x11 0.300 Low Drop 2026-08-01 13:00 [Dropped, Age31]",
                Characterise(rows));
        });
    }

    // Reverse-direction R18 cases from Astra's B4-B6 probes. The last ordinary Move leaves the
    // earlier ten units carrying their own unconfirmed/carried/dropped doubt after the split.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ten_unconfirmed_units_then_one_confirmed_unit_keep_their_own_confirmation(bool tenUnconfirmed)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-07-16T12:01:00+00:00", Magazine, 11, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Location", StackStation,
                result: tenUnconfirmed ? null : "succeed");
            AddArrivalAt(db, 3, "2026-07-16T12:03:00+00:00", Magazine, 1, "Container", SeedCrate, "Location", StackStation);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);
            Assert.Equal(
                tenUnconfirmed
                    ? "x1 0.700 High Move 2026-07-16 12:03 [UnnamedStation] | " +
                      "x10 0.350 Low Move 2026-07-16 12:02 [Unconfirmed, UnnamedStation]"
                    : "x11 0.700 High Move 2026-07-16 12:03 [UnnamedStation]",
                Characterise(rows));
        });
    }

    [Fact]
    public void Ten_carried_units_then_one_ordinary_move_keep_the_carry_doubt_on_the_ten()
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-07-16T12:00:00+00:00", Magazine, 10, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "Location", StackStation, action: "Carry");
            AddArrivalAt(db, 3, "2026-07-16T12:40:00+00:00", Magazine, 1, "Container", SeedCrate, "Location", StackStation);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);
            Assert.Equal(
                "x1 0.700 High Move 2026-07-16 12:40 [FirstSeen, UnnamedStation] | " +
                "x10 0.350 Low Carry 2026-07-16 12:02 [Carried, UnnamedStation]",
                Characterise(rows));
        });
    }

    [Fact]
    public void Ten_dropped_units_then_one_ordinary_move_keep_the_drop_doubt_on_the_ten()
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            AddArrivalAt(db, 1, "2026-07-16T12:00:00+00:00", Magazine, 10, "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, 2, "2026-07-16T12:02:00+00:00", Magazine, 10, "Container", SeedCrate, "World", StackStation, moveType: "Drop");
            AddArrivalAt(db, 3, "2026-07-16T12:40:00+00:00", Magazine, 1, "Container", SeedCrate, "World", StackStation);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Key == StackStation);
            Assert.Equal(
                "x1 1.000 High Move 2026-07-16 12:40 [FirstSeen] | " +
                "x10 0.300 Low Drop 2026-07-16 12:02 [Dropped]",
                Characterise(rows));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Taking_a_mixed_reserve_from_the_player_keeps_the_remaining_groups_resolved_separately(bool mixed)
    {
        WithProvenanceDb(db =>
        {
            AddSession(db, ProvenanceLog);
            if (mixed)
            {
                AddArrivalAt(db, 1, "2026-07-16T12:03:00+00:00", Magazine, 1,
                    "Location", "1000000003", "Equipped", "");
            }

            var countedUnits = mixed ? 10 : 11;
            var countedOffset = mixed ? 2 : 1;
            AddArrivalAt(db, countedOffset, "2026-07-16T12:04:00+00:00", Magazine, countedUnits,
                "Invalid", "", "Container", SeedCrate);
            AddArrivalAt(db, countedOffset + 1, "2026-07-16T12:04:00+00:00", Magazine, countedUnits,
                "Container", SeedCrate, "Equipped", "");

            // The empty source cannot supply two; MoveAnonymous takes them from the newest
            // carried group. The 11-unit control has one provenance; D03 gives its group
            // the ArrivedBy of its latest arrival.
            AddArrivalAt(db, countedOffset + 2, "2026-07-16T12:06:00+00:00", Magazine, 2,
                "Location", "1000000003", "Container", SeedCrate);

            var rows = UnnamedRows(db, Magazine, ProvenanceAsOf, h => h.Root?.Kind == InventoryKind.Equipped);
            Assert.Equal(mixed
                    ? "x8 1.000 High Move 2026-07-16 12:04 [] | " +
                      "x1 1.000 High Move 2026-07-16 12:03 [FirstSeen]"
                    : "x9 1.000 High Move 2026-07-16 12:04 []",
                Characterise(rows));
        });
    }
}
