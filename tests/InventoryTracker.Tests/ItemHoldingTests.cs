using InventoryTracker.Model;
using InventoryTracker.Resolve;

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
}
