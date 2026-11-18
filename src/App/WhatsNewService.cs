namespace InventoryTracker.App;

/// <summary>
/// The "what's new" popup's state for this run: which releases to announce after an update, and
/// remembering in config.json once the user has closed it.
/// </summary>
/// <remarks>
/// Evaluated once per run and shared by every open page, so the window and a browser tab agree.
/// When GitHub cannot be reached nothing is shown and nothing is recorded, so the notes come up
/// on a later launch instead of being lost.
/// </remarks>
public sealed class WhatsNewService
{
    // Unstamped local builds get the compiler's 1.0.0.0 (see UpdateService.CurrentVersion), which
    // sorts above every release: recording it would silence the notes of every real update after.
    private static readonly Version UnstampedBuild = new(1, 0, 0, 0);

    private readonly TrackerOptions _options;
    private readonly ReleaseNotesService _releaseNotes;
    private readonly Lock _gate = new();
    private Task<IReadOnlyList<ReleaseNote>>? _pending;

    public WhatsNewService(TrackerOptions options, ReleaseNotesService releaseNotes)
        : this(options, releaseNotes, UpdateService.CurrentVersion, configPath: null)
    {
    }

    /// <param name="current">The running version; tests pass one in place of the assembly's.</param>
    /// <param name="configPath">The config.json to record in; null for the real one.</param>
    internal WhatsNewService(
        TrackerOptions options, ReleaseNotesService releaseNotes, Version current, string? configPath)
    {
        _options = options;
        _releaseNotes = releaseNotes;
        _configPath = configPath;
        Current = ReleaseNoteParser.Normalize(current);
    }

    private readonly string? _configPath;

    private Version Current { get; }

    /// <summary>The releases the popup should list, newest first; empty when there is no news.</summary>
    /// <remarks>Shared by every page, so no caller's token can cancel it for the others.</remarks>
    public Task<IReadOnlyList<ReleaseNote>> PendingAsync()
    {
        lock (_gate)
            return _pending ??= EvaluateAsync(CancellationToken.None);
    }

    /// <summary>Closes the popup for good: at once for every page of this run, then in config.json.</summary>
    public async Task DismissAsync(CancellationToken ct = default)
    {
        lock (_gate)
            _pending = Task.FromResult<IReadOnlyList<ReleaseNote>>([]);
        await _options.MarkVersionSeenAsync(ReleaseNoteParser.Format(Current), _configPath, ct);
    }

    private async Task<IReadOnlyList<ReleaseNote>> EvaluateAsync(CancellationToken ct)
    {
        var current = Current;
        if (current == UnstampedBuild) return [];

        Version? lastSeen = ReleaseNoteParser.TryParseVersion(_options.LastSeenVersion, out var seen) ? seen : null;
        switch (WhatsNewDecision.Decide(lastSeen, current, _options.SetupComplete))
        {
            case WhatsNewAction.Nothing:
                return [];
            case WhatsNewAction.Record:
                await TryMarkSeenAsync(current, ct);
                return [];
        }

        IReadOnlyList<ReleaseNote> releases;
        try
        {
            releases = await _releaseNotes.GetReleasesAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Notes are decoration on top of an update: no failure here may reach the page.
            // Offline or GitHub unavailable: try again next launch.
            System.Diagnostics.Trace.WriteLine($"What's new skipped: {ex.Message}");
            return [];
        }

        var news = WhatsNewDecision.Select(releases, lastSeen, current);

        // An update made only of internal changes has nothing to say; remember it and stay quiet.
        // Only once GitHub actually lists the running version, though: a list that does not have
        // it yet (or a test build, which never has a release) proves nothing, and recording then
        // would lose the notes for good instead of trying again next launch.
        if (news.Count == 0 && releases.Any(r => r.Version == current))
            await TryMarkSeenAsync(current, ct);
        return news;
    }

    private async Task TryMarkSeenAsync(Version current, CancellationToken ct)
    {
        try
        {
            await _options.MarkVersionSeenAsync(ReleaseNoteParser.Format(current), _configPath, ct);
        }
        catch (Exception ex) when (FormOperationErrors.TryGetMessage(ex, out _))
        {
            // Recorded for this run; the next launch decides again if config.json was not written.
        }
    }
}
