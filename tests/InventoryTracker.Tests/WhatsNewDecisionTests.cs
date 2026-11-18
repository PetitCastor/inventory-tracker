using InventoryTracker.App;

namespace InventoryTracker.Tests;

public sealed class WhatsNewDecisionTests
{
    private static readonly IReadOnlyList<ReleaseNoteEntry> SomeNotes =
        [new ReleaseNoteEntry(ReleaseNoteKind.New, "Something")];

    private static ReleaseNote Release(string version, bool withNotes = true) =>
        new(Version.Parse(version), "v" + version, null, withNotes ? SomeNotes : []);

    [Fact]
    public void A_first_run_records_its_version_without_a_changelog()
    {
        Assert.Equal(WhatsNewAction.Record,
            WhatsNewDecision.Decide(lastSeen: null, new Version(0, 2, 46), setupComplete: false));
    }

    [Fact]
    public void An_existing_user_on_the_first_build_with_the_popup_sees_it()
    {
        Assert.Equal(WhatsNewAction.Show,
            WhatsNewDecision.Decide(lastSeen: null, new Version(0, 2, 46), setupComplete: true));
    }

    [Fact]
    public void An_update_shows_the_popup()
    {
        Assert.Equal(WhatsNewAction.Show,
            WhatsNewDecision.Decide(new Version(0, 2, 45), new Version(0, 2, 46, 0), setupComplete: true));
    }

    [Theory]
    [InlineData("0.2.46", "0.2.46.0")]
    [InlineData("0.2.46.0", "0.2.46")]
    [InlineData("0.2.47", "0.2.46")]
    public void The_same_version_or_a_downgrade_shows_nothing(string lastSeen, string current)
    {
        Assert.Equal(WhatsNewAction.Nothing,
            WhatsNewDecision.Decide(Version.Parse(lastSeen), Version.Parse(current), setupComplete: true));
    }

    [Fact]
    public void A_test_build_above_the_last_release_counts_as_newer()
    {
        Assert.Equal(WhatsNewAction.Show,
            WhatsNewDecision.Decide(new Version(0, 2, 45), new Version(0, 2, 45, 1), setupComplete: true));
    }

    [Fact]
    public void Lists_the_releases_since_the_last_seen_newest_first()
    {
        var releases = new[] { Release("0.2.43.0"), Release("0.2.46.0"), Release("0.2.44.0"), Release("0.2.45.0") };

        var selected = WhatsNewDecision.Select(releases, new Version(0, 2, 44), new Version(0, 2, 46, 0));

        Assert.Equal(new[] { "v0.2.46.0", "v0.2.45.0" }, selected.Select(r => r.TagName));
    }

    [Fact]
    public void Never_lists_a_release_newer_than_the_running_build()
    {
        var releases = new[] { Release("0.2.47.0"), Release("0.2.46.0") };

        var selected = WhatsNewDecision.Select(releases, new Version(0, 2, 45), new Version(0, 2, 46, 0));

        Assert.Equal(new[] { "v0.2.46.0" }, selected.Select(r => r.TagName));
    }

    [Fact]
    public void Releases_with_nothing_for_users_are_left_out()
    {
        var releases = new[] { Release("0.2.46.0", withNotes: false), Release("0.2.45.0") };

        var selected = WhatsNewDecision.Select(releases, new Version(0, 2, 44), new Version(0, 2, 46, 0));

        Assert.Equal(new[] { "v0.2.45.0" }, selected.Select(r => r.TagName));
    }

    [Fact]
    public void With_no_version_seen_only_the_newest_release_is_listed()
    {
        var releases = new[] { Release("0.2.45.0"), Release("0.2.46.0"), Release("0.2.44.0") };

        var selected = WhatsNewDecision.Select(releases, lastSeen: null, new Version(0, 2, 46, 0));

        Assert.Equal(new[] { "v0.2.46.0" }, selected.Select(r => r.TagName));
    }

    [Fact]
    public void With_no_version_seen_an_older_release_is_not_presented_as_news()
    {
        var releases = new[] { Release("0.2.45.0"), Release("0.2.46.0", withNotes: false) };

        Assert.Empty(WhatsNewDecision.Select(releases, lastSeen: null, new Version(0, 2, 46, 0)));
    }

    [Fact]
    public void A_long_gap_is_capped()
    {
        var releases = Enumerable.Range(1, 30).Select(i => Release($"0.2.{i}.0")).ToArray();

        var selected = WhatsNewDecision.Select(releases, new Version(0, 1, 0), new Version(0, 2, 30, 0));

        Assert.Equal(WhatsNewDecision.MaxReleases, selected.Count);
        Assert.Equal("v0.2.30.0", selected[0].TagName);
    }
}
