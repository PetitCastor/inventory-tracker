using System.Net;
using InventoryTracker.App;

namespace InventoryTracker.Tests;

public sealed class ReleaseNotesServiceTests
{
    private const string Releases = """
        [
          { "tag_name": "v0.2.45", "body": "feat: give Settings a section nav (#58)", "published_at": "2026-11-17T19:58:35Z" },
          { "tag_name": "v0.2.47", "body": "fix: draft fix", "draft": true },
          { "tag_name": "v0.2.46", "body": "fix: hide the neck seal (#56)", "prerelease": true },
          { "tag_name": "nightly", "body": "feat: not a version" },
          { "tag_name": "v0.2.44", "body": null },
          { "tag_name": "v0.2.43", "body": "ci: skip the build on closed PRs" }
        ]
        """;

    [Fact]
    public async Task Reads_published_releases_newest_first()
    {
        var handler = new StubJsonHandler(Releases);
        var service = new ReleaseNotesService(new HttpClient(handler));

        var releases = await service.GetReleasesAsync();

        Assert.Equal(new[] { "v0.2.45", "v0.2.44", "v0.2.43" }, releases.Select(r => r.TagName));
        Assert.Equal(new Version(0, 2, 45, 0), releases[0].Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 19, 58, 35, TimeSpan.Zero), releases[0].PublishedAt);
        Assert.Equal("Give Settings a section nav", Assert.Single(releases[0].Entries).Text);
        Assert.Empty(releases[1].Entries);
        Assert.Empty(releases[2].Entries);
        Assert.Equal(ReleaseNotesService.ReleasesUrl, Assert.Single(handler.Requested));
    }

    [Fact]
    public async Task One_read_serves_every_caller()
    {
        var handler = new StubJsonHandler(Releases);
        var service = new ReleaseNotesService(new HttpClient(handler));

        await service.GetReleasesAsync();
        await service.GetReleasesAsync();

        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task A_failed_read_is_retried_by_the_next_caller()
    {
        var handler = new FailOnceHandler(Releases);
        var service = new ReleaseNotesService(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(service.GetReleasesAsync);
        var releases = await service.GetReleasesAsync();

        Assert.Equal(3, releases.Count);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_body_that_is_not_a_list_is_an_error()
    {
        var service = new ReleaseNotesService(new HttpClient(new StubJsonHandler("""{ "message": "rate limited" }""")));

        await Assert.ThrowsAsync<InvalidDataException>(service.GetReleasesAsync);
    }

    private sealed class FailOnceHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
