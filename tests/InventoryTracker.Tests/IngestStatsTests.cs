using InventoryTracker.Ingest;

namespace InventoryTracker.Tests;

public sealed class IngestStatsTests
{
    [Fact]
    public void A_pass_that_read_nothing_changed_no_holdings()
    {
        Assert.False(new IngestStats { FilesSeen = 90, LinesRead = 120, PlaceEvidence = 3 }.ChangedHoldings);
    }

    [Theory]
    [InlineData(nameof(IngestStats.MovesInserted))]
    [InlineData(nameof(IngestStats.StoresRecorded))]
    [InlineData(nameof(IngestStats.CompletionsApplied))]
    [InlineData(nameof(IngestStats.ContainersIdentified))]
    [InlineData(nameof(IngestStats.CapacitiesSeen))]
    [InlineData(nameof(IngestStats.LocationsNamed))]
    [InlineData(nameof(IngestStats.LocationConflicts))]
    [InlineData(nameof(IngestStats.GeidsRecovered))]
    [InlineData(nameof(IngestStats.AttachmentsSeen))]
    [InlineData(nameof(IngestStats.RequestsLost))]
    [InlineData(nameof(IngestStats.UnrecognisedMoves))]
    public void Any_write_the_holdings_are_built_from_needs_a_new_snapshot(string counter)
    {
        // A completion or geid read in a later pass than its move changes what the UI should
        // show without inserting a move; only counting moves left the snapshot behind.
        var stats = new IngestStats();
        typeof(IngestStats).GetProperty(counter)!.SetValue(stats, 1);

        Assert.True(stats.ChangedHoldings);
    }
}
