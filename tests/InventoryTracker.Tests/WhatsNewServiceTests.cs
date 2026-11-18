using System.Net;
using InventoryTracker.App;

namespace InventoryTracker.Tests;

public sealed class WhatsNewServiceTests : IDisposable
{
    private const string Releases = """
        [
          { "tag_name": "v0.2.46", "body": "feat: an app window (#60)" },
          { "tag_name": "v0.2.45", "body": "feat: give Settings a section nav (#58)" },
          { "tag_name": "v0.2.44", "body": "ci: internal only" }
        ]
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-whatsnew-{Guid.NewGuid():N}");
    private readonly string _configPath;

    public WhatsNewServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private WhatsNewService Create(
        TrackerOptions options, Version current, HttpMessageHandler? handler = null) =>
        new(options, new ReleaseNotesService(new HttpClient(handler ?? new StubJsonHandler(Releases))), current, _configPath);

    [Fact]
    public async Task After_an_update_lists_what_changed_until_closed()
    {
        var options = new TrackerOptions { SetupComplete = true, LastSeenVersion = "0.2.44" };
        var whatsNew = Create(options, new Version(0, 2, 46, 0));

        var pending = await whatsNew.PendingAsync();
        Assert.Equal(new[] { "v0.2.46", "v0.2.45" }, pending.Select(r => r.TagName));
        Assert.Equal("0.2.44", options.LastSeenVersion);

        await whatsNew.DismissAsync();

        Assert.Empty(await whatsNew.PendingAsync());
        Assert.Equal("0.2.46", options.LastSeenVersion);
        Assert.Equal("0.2.46", AppConfig.Load(_configPath).LastSeenVersion);
    }

    [Fact]
    public async Task A_first_run_records_its_version_without_asking_github()
    {
        var handler = new StubJsonHandler(Releases);
        var options = new TrackerOptions { SetupComplete = false };
        var whatsNew = Create(options, new Version(0, 2, 46, 0), handler);

        Assert.Empty(await whatsNew.PendingAsync());

        Assert.Equal("0.2.46", AppConfig.Load(_configPath).LastSeenVersion);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task An_update_with_only_internal_changes_is_recorded_quietly()
    {
        var options = new TrackerOptions { SetupComplete = true, LastSeenVersion = "0.2.43" };
        var whatsNew = Create(options, new Version(0, 2, 44, 0));

        Assert.Empty(await whatsNew.PendingAsync());

        Assert.Equal("0.2.44", AppConfig.Load(_configPath).LastSeenVersion);
    }

    [Fact]
    public async Task A_list_that_does_not_have_the_running_version_yet_records_nothing()
    {
        var options = new TrackerOptions { SetupComplete = true, LastSeenVersion = "0.2.46" };
        var whatsNew = Create(options, new Version(0, 2, 47, 0));

        Assert.Empty(await whatsNew.PendingAsync());

        Assert.Equal("0.2.46", options.LastSeenVersion);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public async Task Offline_shows_nothing_and_keeps_the_notes_for_a_later_launch()
    {
        var options = new TrackerOptions { SetupComplete = true, LastSeenVersion = "0.2.44" };
        var whatsNew = Create(options, new Version(0, 2, 46, 0), new UnavailableHandler());

        Assert.Empty(await whatsNew.PendingAsync());

        Assert.Equal("0.2.44", options.LastSeenVersion);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public async Task An_unstamped_build_never_records_its_placeholder_version()
    {
        var handler = new StubJsonHandler(Releases);
        var options = new TrackerOptions { SetupComplete = false };
        var whatsNew = Create(options, new Version(1, 0, 0, 0), handler);

        Assert.Empty(await whatsNew.PendingAsync());

        Assert.Null(options.LastSeenVersion);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public async Task The_same_version_again_shows_nothing()
    {
        var handler = new StubJsonHandler(Releases);
        var options = new TrackerOptions { SetupComplete = true, LastSeenVersion = "0.2.46" };
        var whatsNew = Create(options, new Version(0, 2, 46, 0), handler);

        Assert.Empty(await whatsNew.PendingAsync());
        Assert.Empty(handler.Requested);
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
