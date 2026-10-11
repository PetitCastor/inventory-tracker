using InventoryTracker.Naming;

namespace InventoryTracker.Tests;

public class PlaceTextKeysTests
{
    /// <summary>Entries as they appear in the game's global.ini, siblings included.</summary>
    private static readonly GameLocalization Text = GameLocalization.Parse(
    [
        "﻿2019_Ann_Sale_Day1=Expo-hall Day 01",
        "Stanton3_Area18=Area18",
        "Stanton3_Area18_Desc=Area18 is one of the main commercial ports",
        "RR_CRU_L4=CRU-L4 Shallow Fields Station",
        "RR_P6=Ruin Station",
        "Stanton3_Transfer=Baijini Point",
        "Stanton3_Transfer_Desc=The primary cargo and transfer point for ArcCorp.",
        "Stanton2_Transfer_Seraphim=Seraphim Station",
        "Stanton2_Transfer_Seraphim_Desc=Located above Crusader",
        "Stanton2_Transfer_Seraphim_clinic=Seraphim Clinic",
        "Nyx_Pyro_JPStation=Pyro Gateway",
        "Nyx_SocialStation_001=People's Service Station Alpha",
        "Nyx_SocialStation_003=People's Service Station Theta",
        "Nyx_Keeger_Segment_Social_001,P=Keeger Social Station 001",
    ]);

    [Theory]
    [InlineData("Stanton3_Area18", "Area18")]
    [InlineData("RR_CRU_L4", "CRU-L4 Shallow Fields Station")]
    public void A_string_that_is_its_own_key_reads_straight_off(string raw, string name)
    {
        Assert.Equal(name, PlaceTextKeys.NameFrom(raw, Text));
    }

    [Theory]
    [InlineData("RR_P6_LEO", "Ruin Station")]
    [InlineData("RR_ARC_LEO", "Baijini Point")]
    [InlineData("RR_CRU_LEO", "Seraphim Station")] // the one name below Stanton2_Transfer
    public void A_low_orbit_station_reads_from_its_planets_station_or_transfer_point(string raw, string name)
    {
        Assert.Equal(name, PlaceTextKeys.NameFrom(raw, Text));
    }

    [Fact]
    public void A_gateway_reads_from_the_jump_station_between_its_two_systems()
    {
        Assert.Equal("Pyro Gateway", PlaceTextKeys.NameFrom("RR_JP_NyxPyro", Text));
    }

    [Theory]
    [InlineData("AsteroidClusterBase_Nyx_Social_Keeger_001", "People's Service Station Alpha")]
    [InlineData("AsteroidClusterBase_Nyx_Social_Keeger_003", "People's Service Station Theta")]
    public void A_belt_station_reads_from_its_systems_social_station(string raw, string name)
    {
        Assert.Equal(name, PlaceTextKeys.NameFrom(raw, Text));
    }

    [Theory]
    [InlineData("RR_JP_NyxCastra")]                           // no Nyx_Castra_JPStation: not guessed
    [InlineData("AsteroidClusterBase_Nyx_Social_Keeger_002")] // no _002 entry
    [InlineData("RR_HUR_LEO")]                                // no Stanton1_Transfer entry
    [InlineData("Nyx_Levski")]
    public void A_key_that_is_not_in_the_file_proposes_nothing(string raw)
    {
        Assert.Null(PlaceTextKeys.NameFrom(raw, Text));
    }

    [Fact]
    public void A_flagged_key_is_read_without_its_flag()
    {
        Assert.Equal("Keeger Social Station 001", Text.Get("Nyx_Keeger_Segment_Social_001"));
        Assert.Equal("Expo-hall Day 01", Text.Get("2019_Ann_Sale_Day1")); // the byte-order mark is not part of the key
    }

    [Fact]
    public void Two_names_below_a_prefix_are_ambiguous()
    {
        var text = GameLocalization.Parse(["Stanton2_Transfer_A=One", "Stanton2_Transfer_B=Two"]);

        Assert.Null(text.GetOnlyChild("Stanton2_Transfer"));
    }

    [Fact]
    public void The_loose_file_is_read_in_the_language_the_client_is_set_to()
    {
        var install = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(install, "data", "Localization", "french_(france)"));
            File.WriteAllText(Path.Combine(install, "user.cfg"), "g_language = french_(france)\r\n");
            File.WriteAllText(
                Path.Combine(install, "data", "Localization", "french_(france)", "global.ini"),
                "Stanton3_Area18=Area18 (FR)\r\n");

            Assert.Equal("Area18 (FR)", GameLocalization.Load(install).Get("Stanton3_Area18"));
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public void An_install_without_a_loose_file_has_no_text()
    {
        Assert.Equal(0, GameLocalization.Load(Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}")).Count);
    }
}
