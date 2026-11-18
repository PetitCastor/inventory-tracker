using System.Net.Http.Json;
using System.Text.Json;

namespace InventoryTracker.App;

/// <summary>
/// The tracker's release history, read from GitHub Releases: every tag with the notes its body
/// lists (see <see cref="ReleaseNoteParser"/>). Read once per run and shared by the "what's new"
/// popup and Settings → Updates; a failed read is not kept, so a later call tries again.
/// </summary>
public sealed class ReleaseNotesService(HttpClient http)
{
    // One page of 50 is months of releases at one per merge; "what's new" never needs more.
    internal const string ReleasesUrl = "https://api.github.com/repos/PetitCastor/inventory-tracker/releases?per_page=50";

    private readonly Lock _gate = new();
    private Task<IReadOnlyList<ReleaseNote>>? _releases;

    /// <summary>
    /// Published releases, newest first. Drafts and pre-releases are left out, as are tags that
    /// are not versions. Throws on a network or parse failure; callers show the notes as
    /// unavailable and carry on. Shared by every caller, so it takes no token: one page closing
    /// must not cancel the read for another; <see cref="FetchTimeout"/> bounds it instead.
    /// </summary>
    public Task<IReadOnlyList<ReleaseNote>> GetReleasesAsync()
    {
        lock (_gate)
        {
            if (_releases is null || _releases.IsFaulted || _releases.IsCanceled)
                _releases = FetchAsync();
            return _releases;
        }
    }

    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private async Task<IReadOnlyList<ReleaseNote>> FetchAsync()
    {
        using var timeout = new CancellationTokenSource(FetchTimeout);
        using var doc = await http.GetFromJsonAsync<JsonDocument>(ReleasesUrl, timeout.Token)
            ?? throw new InvalidDataException("GitHub returned no release list.");
        return Parse(doc.RootElement);
    }

    internal static IReadOnlyList<ReleaseNote> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub's release list was not an array.");

        var releases = new List<ReleaseNote>();
        foreach (var release in root.EnumerateArray())
        {
            if (IsTrue(release, "draft") || IsTrue(release, "prerelease")) continue;

            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (!ReleaseNoteParser.TryParseVersion(tag, out var version)) continue;

            var body = release.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            DateTimeOffset? published =
                release.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                && p.TryGetDateTimeOffset(out var at)
                    ? at
                    : null;

            releases.Add(new ReleaseNote(version, tag!, published, ReleaseNoteParser.ParseBody(body)));
        }

        releases.Sort((a, b) => b.Version.CompareTo(a.Version));
        return releases;
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
