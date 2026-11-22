using InventoryTracker.Resolve;

namespace InventoryTracker.Tests;

/// <summary>
/// Tying a locker to its ship by geid, and telling a claimed ship from one only stored and
/// retrieved. Geids and offsets are those of the logs: a Shiv's locker 173 above it, the
/// Asgard's <c>PersonalStorage_Template-001</c> 348 above it.
/// </summary>
public sealed class ShipFleetTests
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 21, 19, 37, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Retrieved = new(2026, 11, 21, 19, 18, 54, TimeSpan.Zero);

    private static ShipRecord Ship(long geid, string? cls, bool retrieved = true) =>
        new(geid, cls, Seen, retrieved ? Retrieved : null, retrieved ? "2273540638" : null);

    [Fact]
    public void A_template_locker_belongs_to_the_ship_just_below_it()
    {
        var asgard = Ship(947187047401, "ANVL_Asgard");
        var fleet = new ShipFleet([Ship(946557996936, "GLSN_Shiv"), asgard, Ship(947923875585, "GLSN_Shiv")]);

        Assert.Equal(asgard, fleet.OwnerOf("947187047749", "Template"));
    }

    [Fact]
    public void A_named_locker_belongs_to_its_ship()
    {
        var shiv = Ship(876737712932, "GLSN_Shiv");
        var fleet = new ShipFleet([shiv]);

        Assert.Equal(shiv, fleet.OwnerOf("876737713105", "GLSN_Shiv"));
        Assert.Equal(shiv, fleet.OwnerOf("876737713106", "GLSN_Shiv"));
    }

    [Theory]
    [InlineData("947187047749", "GLSN_Shiv")]      // the ship below is an Asgard
    [InlineData("947187048402", "Template")]       // 1001 above the ship
    [InlineData("947187047401", "Template")]       // the ship's own geid
    [InlineData("947187047000", "Template")]       // below every ship
    [InlineData("not-a-geid", "Template")]
    public void A_locker_with_no_ship_just_below_it_has_no_owner(string locker, string lockerClass)
    {
        var fleet = new ShipFleet([Ship(947187047401, "ANVL_Asgard")]);

        Assert.Null(fleet.OwnerOf(locker, lockerClass));
    }

    [Fact]
    public void A_locker_may_sit_up_to_a_thousand_above_its_ship()
    {
        var asgard = Ship(947187047401, "ANVL_Asgard");
        var fleet = new ShipFleet([asgard]);

        Assert.Equal(asgard, fleet.OwnerOf("947187048401", "Template"));
    }

    [Fact]
    public void A_named_locker_skips_a_ship_of_another_class_spawned_just_after_its_own()
    {
        var shiv = Ship(947923875585, "GLSN_Shiv");
        var fleet = new ShipFleet([shiv, Ship(947923875600, "ANVL_Asgard")]);

        Assert.Equal(shiv, fleet.OwnerOf("947923875758", "GLSN_Shiv"));
    }

    [Fact]
    public void A_ship_never_piloted_still_owns_its_named_locker()
    {
        // Only a retrieval saw it, so the vehicle row has no class; the locker's class is trusted.
        var shiv = Ship(876737712932, cls: null);
        var fleet = new ShipFleet([shiv]);

        Assert.Equal(shiv, fleet.OwnerOf("876737713105", "GLSN_Shiv"));
    }

    [Fact]
    public void A_newer_ship_of_the_class_called_out_replaces_the_older()
    {
        var old = Ship(906670858598, "GLSN_Shiv");
        var fleet = new ShipFleet([old, Ship(947187047401, "ANVL_Asgard"), Ship(947923875585, "GLSN_Shiv")]);

        Assert.Equal(947923875585, fleet.ReplacementOf(old)?.Geid);
    }

    [Fact]
    public void The_newest_ship_of_its_class_is_not_replaced()
    {
        var newest = Ship(947923875585, "GLSN_Shiv");
        var fleet = new ShipFleet([Ship(906670858598, "GLSN_Shiv"), newest]);

        Assert.Null(fleet.ReplacementOf(newest));
    }

    [Fact]
    public void A_newer_ship_only_flown_does_not_replace_the_players_own()
    {
        // Someone else's Shiv, piloted but never called out of the player's storage.
        var own = Ship(906670858598, "GLSN_Shiv");
        var fleet = new ShipFleet([own, Ship(947923875585, "GLSN_Shiv", retrieved: false)]);

        Assert.Null(fleet.ReplacementOf(own));
    }

    [Fact]
    public void A_newer_ship_of_another_class_does_not_replace_it()
    {
        var shiv = Ship(906670858598, "GLSN_Shiv");
        var fleet = new ShipFleet([shiv, Ship(947187047401, "ANVL_Asgard")]);

        Assert.Null(fleet.ReplacementOf(shiv));
    }

    [Fact]
    public void A_ship_of_unknown_class_takes_the_class_its_locker_names()
    {
        var old = Ship(906670858598, cls: null);
        var fleet = new ShipFleet([old, Ship(947923875585, "GLSN_Shiv")]);

        Assert.Null(fleet.ReplacementOf(old));
        Assert.Equal(947923875585, fleet.ReplacementOf(old, "GLSN_Shiv")?.Geid);
    }

    [Theory]
    [InlineData("Template", true)]
    [InlineData("template", true)]
    [InlineData("GLSN_Shiv", false)]
    public void Only_template_is_a_generic_locker(string lockerClass, bool generic) =>
        Assert.Equal(generic, ShipFleet.IsGeneric(lockerClass));
}
