using InventoryTracker.Model;

namespace InventoryTracker.Tests;

public sealed class ShipStorageTests
{
    [Theory]
    [InlineData("PersonalStorage_GLSN_Shiv-000", "GLSN_Shiv")]
    [InlineData("PersonalStorage_GLSN_Shiv-001", "GLSN_Shiv")]
    [InlineData("PersonalStorage_ARGO_MOLE_BTALA-000", "ARGO_MOLE_BTALA")]
    public void A_locker_names_its_ship_class(string container, string ship)
    {
        Assert.True(ShipStorage.TryShipClass(container, out var shipClass));
        Assert.Equal(ship, shipClass);
    }

    [Theory]
    [InlineData("qrt_utility_heavy_backpack_01_01_03")]
    [InlineData("Lootable_Container_Large")]
    [InlineData("PersonalStorage_GLSN_Shiv")]
    [InlineData(null)]
    public void Anything_else_is_not_a_locker(string? container) =>
        Assert.False(ShipStorage.TryShipClass(container, out _));
}
