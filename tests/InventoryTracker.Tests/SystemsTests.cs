using InventoryTracker.Naming;

namespace InventoryTracker.Tests;

public class SystemsTests
{
    [Theory]
    [InlineData("Data/objectcontainers/pu/loc/mod/nyx/station/ser/reststop_ext/rs.socpak", "Nyx")]
    [InlineData("Data/objectcontainers/pu/loc/flagship/stanton/area18.socpak", "Stanton")]
    [InlineData("Data/objectcontainers/pu/loc/pyro/whatever.socpak", "Pyro")]
    public void Finds_the_system_in_an_object_container_path(string path, string expected)
    {
        Assert.Equal(expected, Systems.FromAssetPath(path));
    }

    [Theory]
    // Asset categories sit at the same depth as systems, so only known names count.
    [InlineData("Data/objectcontainers/pu/loc/mod/ugf/bunker.socpak")]
    [InlineData("Data/objectcontainers/pu/loc/common/props.socpak")]
    [InlineData("Data/somewhere/else/entirely.socpak")]
    public void Returns_null_when_no_segment_names_a_system(string path)
    {
        Assert.Null(Systems.FromAssetPath(path));
    }

    [Theory]
    [InlineData("Stanton")]
    [InlineData("Stanton System")]
    [InlineData("  Pyro  ")]
    [InlineData("nyx")]
    public void Recognises_a_name_that_is_only_a_system(string displayName)
    {
        // Reported while flying through open space; adopting one as a station's name would
        // label the place after the region it sits in.
        Assert.True(Systems.IsSystemOnly(displayName));
    }

    [Theory]
    [InlineData("Stanton Gateway")]
    [InlineData("Area18")]
    [InlineData("Port Olisar")]
    public void Does_not_mistake_a_real_place_for_a_system(string displayName)
    {
        Assert.False(Systems.IsSystemOnly(displayName));
    }

    [Fact]
    public void Canonicalises_casing()
    {
        Assert.Equal("Nyx", Systems.Canonical("nyx"));
        Assert.Equal("Stanton", Systems.Canonical("STANTON"));
    }
}

public class PlaceCatalogSystemTests
{
    [Theory]
    // "RR_JP_NyxCastra" is the Nyx-side stop of the Nyx<->Castra jump point; the first
    // system named is the host. The place's *name* is wrong in these strings, the system
    // is not.
    [InlineData("RR_JP_NyxCastra", "Nyx")]
    [InlineData("RR_JP_PyroNyx", "Pyro")]
    public void Reads_the_host_system_from_a_jump_point_pair(string raw, string expected)
    {
        Assert.Equal(expected, PlaceCatalog.SystemFromRaw(raw));
    }

    [Theory]
    [InlineData("Stanton_Kaboos", "Stanton")]
    [InlineData("Nyx_Kaboos", "Nyx")]
    [InlineData("RS_HUR_L1", "Stanton")]
    [InlineData("RS_P5_L2", "Pyro")]
    public void Reads_the_system_from_a_planet_token(string raw, string expected)
    {
        Assert.Equal(expected, PlaceCatalog.SystemFromRaw(raw));
    }

    [Fact]
    public void Leaves_an_undecidable_string_explicitly_unknown()
    {
        // Guessing here would bury the problem; null surfaces it in diagnostics and can be
        // corrected in place_override.json.
        Assert.Null(PlaceCatalog.SystemFromRaw("SOMETHING_UNRECOGNISED"));
    }
}
