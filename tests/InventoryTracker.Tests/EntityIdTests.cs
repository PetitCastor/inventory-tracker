using InventoryTracker.Model;

namespace InventoryTracker.Tests;

public class EntityIdTests
{
    [Theory]
    [InlineData("grin_multitool_01_red01_481104483777", "grin_multitool_01_red01", "481104483777")]
    [InlineData("qrt_utility_heavy_backpack_01_01_03_745829874584", "qrt_utility_heavy_backpack_01_01_03", "745829874584")]
    public void Splits_class_from_trailing_geid(string entity, string expectedClass, string expectedGeid)
    {
        Assert.True(EntityId.TrySplit(entity, out var className, out var geid));
        Assert.Equal(expectedClass, className);
        Assert.Equal(expectedGeid, geid);
    }

    [Fact]
    public void Keeps_digits_that_belong_to_the_class_name()
    {
        // The class part is matched lazily on purpose: class names routinely end in digits,
        // and a greedy match would slice the 12-digit geid in half and keep the tail.
        Assert.True(EntityId.TrySplit("behr_rifle_ballistic_01_01_01_123456789012", out var cls, out var geid));
        Assert.Equal("behr_rifle_ballistic_01_01_01", cls);
        Assert.Equal("123456789012", geid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NULL")]
    [InlineData("NONE")]
    [InlineData("INVALID")]
    [InlineData("no_geid_here")]
    [InlineData("too_short_12345")]
    public void Rejects_anything_that_is_not_class_underscore_geid(string? entity)
    {
        Assert.False(EntityId.TrySplit(entity, out var cls, out var geid));
        Assert.Equal("", cls);
        Assert.Equal("", geid);
    }
}
