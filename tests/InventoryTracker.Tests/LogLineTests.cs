using InventoryTracker.Ingest;
using InventoryTracker.Model;

namespace InventoryTracker.Tests;

public class LogLineTests
{
    [Fact]
    public void Splits_a_structured_line_into_its_parts()
    {
        Assert.True(LogLine.TryParse(
            "<2026-07-16T11:20:52.355Z> [Notice] <Inventory Token Flow> the rest of it", out var line));

        Assert.Equal("Notice", line.Severity);
        Assert.Equal("Inventory Token Flow", line.EventName);
        Assert.Equal("the rest of it", line.Rest);
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 11, 20, 52, 355, TimeSpan.Zero), line.Timestamp);
    }

    [Fact]
    public void Accepts_an_event_name_that_is_not_word_safe()
    {
        // Event names contain spaces, "::" and even "<lambda_1"; only the outer angle
        // brackets are reliable.
        Assert.True(LogLine.TryParse("<2026-07-16T11:20:52.355Z> [Notice] <lambda_1> body", out var line));
        Assert.Equal("lambda_1", line.EventName);
    }

    [Fact]
    public void Normalises_the_timestamp_to_utc()
    {
        Assert.True(LogLine.TryParse("<2026-07-16T11:20:52.355Z> [Notice] <X> y", out var line));
        Assert.Equal(TimeSpan.Zero, line.Timestamp.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a log line at all")]
    [InlineData("[Notice] <X> missing the timestamp")]
    [InlineData("<not-a-date> [Notice] <X> y")]
    public void Rejects_anything_that_is_not_the_expected_shape(string raw)
    {
        Assert.False(LogLine.TryParse(raw, out var line));
        Assert.Null(line);
    }
}

public class InventoryRefTests
{
    [Fact]
    public void Parses_a_location_handle()
    {
        var reference = InventoryRef.Parse("204821708183:Location:4005457614");

        Assert.Equal(InventoryKind.Location, reference.Kind);
        Assert.Equal("4005457614", reference.HoldingKey);
        Assert.True(reference.IsHolding);
    }

    [Fact]
    public void Parses_a_container_handle_and_keys_it_by_geid()
    {
        var reference = InventoryRef.Parse("681562156430:Container:0");

        Assert.Equal(InventoryKind.Container, reference.Kind);
        Assert.Equal("681562156430", reference.HoldingKey);
    }

    [Fact]
    public void Treats_a_client_only_handle_as_not_a_holding()
    {
        var reference = InventoryRef.Parse("0:ClientOnly:1");

        Assert.Equal(InventoryKind.ClientOnly, reference.Kind);
        Assert.False(reference.IsHolding);
        Assert.Equal("", reference.HoldingKey);
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("NULL")]
    [InlineData("NONE")]
    [InlineData("")]
    [InlineData(null)]
    public void Maps_the_empty_spellings_to_invalid(string? raw)
    {
        Assert.Equal(InventoryKind.Invalid, InventoryRef.Parse(raw).Kind);
    }

    [Fact]
    public void Keeps_an_unrecognised_shape_so_diagnostics_can_surface_it()
    {
        var reference = InventoryRef.Parse("something_new");

        Assert.Equal(InventoryKind.Unknown, reference.Kind);
        Assert.Equal("something_new", reference.Raw);
        Assert.False(reference.IsHolding);
    }
}
