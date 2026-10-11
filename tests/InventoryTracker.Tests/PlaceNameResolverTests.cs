using InventoryTracker.Naming;

namespace InventoryTracker.Tests;

public class PlaceNameResolverTests
{
    private const string Keeger001 = "AsteroidClusterBase_Nyx_Social_Keeger_001";

    private static GameLocalization Text(params string[] lines) => GameLocalization.Parse(lines);

    private static WikiLocations Wiki(params WikiLocation[] places) => new(places);

    private static WikiLocation Station(string name, string system, string? planet = null) =>
        new(name, system, planet ?? system, planet is null ? "Star" : "Planet", "Manmade");

    [Fact]
    public void The_games_text_backed_by_the_wiki_confirms_a_station_the_logs_never_named()
    {
        var resolution = PlaceNameResolver.Resolve(
            Keeger001, "Nyx", loggedName: null,
            Text("Nyx_SocialStation_001=People's Service Station Alpha"),
            Wiki(Station("People's Service Station Alpha", "Nyx")));

        Assert.Equal("People's Service Station Alpha", resolution.Name);
        Assert.Equal(PlaceNameStatus.Confirmed, resolution.Status);
        Assert.Equal(PlaceNameSource.GameText | PlaceNameSource.Wiki, resolution.Sources);
    }

    [Fact]
    public void A_single_source_is_only_a_proposal()
    {
        var resolution = PlaceNameResolver.Resolve(
            Keeger001, "Nyx", loggedName: null,
            Text("Nyx_SocialStation_001=People's Service Station Alpha"),
            WikiLocations.Empty);

        Assert.Equal("People's Service Station Alpha", resolution.Name);
        Assert.Equal(PlaceNameStatus.Pending, resolution.Status);
    }

    [Fact]
    public void Nothing_to_go_on_leaves_the_place_unresolved()
    {
        var resolution = PlaceNameResolver.Resolve(
            Keeger001, "Nyx", loggedName: null, GameLocalization.Empty,
            Wiki(Station("People's Service Station Alpha", "Nyx")));

        Assert.Null(resolution.Name);
        Assert.Equal(PlaceNameStatus.Unresolved, resolution.Status);
    }

    [Fact]
    public void The_logs_and_the_built_in_list_agreeing_confirm_without_the_wiki()
    {
        var resolution = PlaceNameResolver.Resolve(
            "Stanton3_Area18", "Stanton", loggedName: "Area18", GameLocalization.Empty, WikiLocations.Empty);

        Assert.Equal("Area18", resolution.Name);
        Assert.Equal(PlaceNameStatus.Confirmed, resolution.Status);
        Assert.Equal(PlaceNameSource.Log | PlaceNameSource.KnownPlaces, resolution.Sources);
    }

    [Fact]
    public void The_wiki_only_backs_a_name_in_the_same_system()
    {
        // There is a Stanton Gateway in Pyro and another in Nyx.
        var wiki = Wiki(Station("Stanton Gateway", "Pyro"));

        var inNyx = PlaceNameResolver.Resolve("RR_JP_NyxCastra", "Nyx", "Stanton Gateway", GameLocalization.Empty, wiki);
        var inPyro = PlaceNameResolver.Resolve("RR_JP_PyroStanton", "Pyro", "Stanton Gateway", GameLocalization.Empty, wiki);

        Assert.Equal(PlaceNameStatus.Pending, inNyx.Status);
        Assert.Equal(PlaceNameStatus.Confirmed, inPyro.Status);
    }

    [Fact]
    public void Two_names_each_backed_twice_are_contested_and_the_logs_name_is_proposed()
    {
        var resolution = PlaceNameResolver.Resolve(
            Keeger001, "Nyx", loggedName: "People's Service Station Theta",
            Text("Nyx_SocialStation_001=People's Service Station Alpha"),
            Wiki(Station("People's Service Station Alpha", "Nyx"), Station("People's Service Station Theta", "Nyx")));

        Assert.Equal("People's Service Station Theta", resolution.Name);
        Assert.Equal(PlaceNameStatus.Pending, resolution.Status);
    }

    [Fact]
    public void The_better_backed_name_wins_over_the_built_in_list()
    {
        // The built-in list says "Checkmate Station"; the game and the wiki say "Checkmate".
        var resolution = PlaceNameResolver.Resolve(
            "RR_P2_L4", "Pyro", loggedName: null,
            Text("RR_P2_L4=Checkmate"),
            Wiki(Station("Checkmate", "Pyro")));

        Assert.Equal("Checkmate", resolution.Name);
        Assert.Equal(PlaceNameStatus.Confirmed, resolution.Status);
    }

    [Fact]
    public void The_wiki_spells_the_name_and_says_which_planet_it_is_on()
    {
        var resolution = PlaceNameResolver.Resolve(
            "RR_P6_LEO", "Pyro", loggedName: "ruin station", GameLocalization.Empty,
            Wiki(Station("Ruin Station", "Pyro", planet: "Terminus")));

        Assert.Equal("Ruin Station", resolution.Name);
        Assert.Equal("Terminus", resolution.Planet);
    }
}
