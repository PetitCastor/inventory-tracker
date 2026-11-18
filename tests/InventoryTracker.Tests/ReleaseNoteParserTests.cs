using InventoryTracker.App;

namespace InventoryTracker.Tests;

public sealed class ReleaseNoteParserTests
{
    [Fact]
    public void Squash_subjects_become_user_facing_notes()
    {
        var entries = ReleaseNoteParser.ParseBody(
            "feat: put the station picker before search on Locations (#57)");

        var entry = Assert.Single(entries);
        Assert.Equal(ReleaseNoteKind.New, entry.Kind);
        Assert.Equal("Put the station picker before search on Locations", entry.Text);
    }

    [Theory]
    [InlineData("feat: x", ReleaseNoteKind.New)]
    [InlineData("FEAT: x", ReleaseNoteKind.New)]
    [InlineData("feat(ui): x", ReleaseNoteKind.New)]
    [InlineData("feat!: x", ReleaseNoteKind.New)]
    [InlineData("perf: x", ReleaseNoteKind.Improved)]
    [InlineData("fix: x", ReleaseNoteKind.Fixed)]
    [InlineData("fix(ingest)!: x", ReleaseNoteKind.Fixed)]
    public void The_type_decides_the_kind(string line, ReleaseNoteKind expected)
    {
        Assert.Equal(expected, Assert.Single(ReleaseNoteParser.ParseBody(line)).Kind);
    }

    [Theory]
    [InlineData("ci: skip the build on closed PRs")]
    [InlineData("docs: update README for character resets (#53)")]
    [InlineData("test: cover the reader")]
    [InlineData("chore: tidy")]
    [InlineData("refactor: split TrayHost")]
    [InlineData("Bump the nuget group with 2 updates (#51)")]
    [InlineData("Merge pull request #12 from PetitCastor/feat/x")]
    [InlineData("feat:")]
    [InlineData("feat: (#12)")]
    [InlineData("")]
    public void Development_history_is_left_out(string line)
    {
        Assert.Empty(ReleaseNoteParser.ParseBody(line));
    }

    [Fact]
    public void A_bom_or_a_markdown_bullet_does_not_hide_a_line()
    {
        var entries = ReleaseNoteParser.ParseBody(
            "\uFEFFfeat: give Settings a section nav and shorter copy (#58)\r\n- fix: hide the neck seal\n* perf: faster replay");

        Assert.Equal(
            new[]
            {
                new ReleaseNoteEntry(ReleaseNoteKind.New, "Give Settings a section nav and shorter copy"),
                new ReleaseNoteEntry(ReleaseNoteKind.Fixed, "Hide the neck seal"),
                new ReleaseNoteEntry(ReleaseNoteKind.Improved, "Faster replay"),
            },
            entries);
    }

    [Fact]
    public void No_body_means_no_notes()
    {
        Assert.Empty(ReleaseNoteParser.ParseBody(null));
        Assert.Empty(ReleaseNoteParser.ParseBody("   \n "));
    }

    [Theory]
    [InlineData("v0.2.45", 0, 2, 45, 0)]
    [InlineData("V1.2.3", 1, 2, 3, 0)]
    [InlineData("0.2.45.1", 0, 2, 45, 1)]
    [InlineData("v0.3", 0, 3, 0, 0)]
    public void Tags_parse_to_four_part_versions(string tag, int major, int minor, int build, int revision)
    {
        Assert.True(ReleaseNoteParser.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void Tags_that_are_not_versions_are_refused(string? tag)
    {
        Assert.False(ReleaseNoteParser.TryParseVersion(tag, out _));
    }

    [Fact]
    public void A_three_part_tag_compares_equal_to_the_assemblys_four_part_version()
    {
        Assert.True(ReleaseNoteParser.TryParseVersion("v0.2.45", out var tag));

        Assert.Equal(ReleaseNoteParser.Normalize(new Version(0, 2, 45, 0)), tag);
        Assert.True(tag < ReleaseNoteParser.Normalize(new Version(0, 2, 45, 1)));
    }

    [Theory]
    [InlineData(0, 2, 45, 0, "0.2.45")]
    [InlineData(0, 2, 45, 1, "0.2.45.1")]
    public void Versions_are_shown_with_three_parts_unless_a_test_build(
        int major, int minor, int build, int revision, string expected)
    {
        Assert.Equal(expected, ReleaseNoteParser.Format(new Version(major, minor, build, revision)));
    }
}
