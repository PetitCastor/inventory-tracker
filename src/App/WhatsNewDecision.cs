namespace InventoryTracker.App;

/// <summary>
/// When the "what's new" popup appears, and what it lists, as pure functions of the version last
/// announced and the version running. A new user is not greeted with a changelog, a downgrade
/// does not re-announce old news, and no version is announced twice.
/// </summary>
public static class WhatsNewDecision
{
    /// <summary>Releases listed at most, so a long gap between launches stays readable.</summary>
    public const int MaxReleases = 10;

    /// <param name="lastSeen">The version last announced, or null when none ever was.</param>
    /// <param name="current">The running version.</param>
    /// <param name="setupComplete">
    /// Tells the two kinds of "never announced" apart: a user who has finished setup was already
    /// using a version from before this popup existed, and gets the current version's notes; a
    /// first run has nothing to compare with and only records the version.
    /// </param>
    public static WhatsNewAction Decide(Version? lastSeen, Version current, bool setupComplete)
    {
        if (lastSeen is null) return setupComplete ? WhatsNewAction.Show : WhatsNewAction.Record;

        return ReleaseNoteParser.Normalize(current) > ReleaseNoteParser.Normalize(lastSeen)
            ? WhatsNewAction.Show
            : WhatsNewAction.Nothing;
    }

    /// <summary>
    /// The releases to list, newest first: those after <paramref name="lastSeen"/> up to and
    /// including <paramref name="current"/>, with at least one note. With no version seen, only
    /// the newest release up to <paramref name="current"/> is considered, and listed only if it
    /// has notes: an older one would announce changes the user has had for weeks.
    /// </summary>
    public static IReadOnlyList<ReleaseNote> Select(
        IEnumerable<ReleaseNote> releases, Version? lastSeen, Version current, int max = MaxReleases)
    {
        var upTo = ReleaseNoteParser.Normalize(current);
        var after = lastSeen is null ? null : ReleaseNoteParser.Normalize(lastSeen);

        var released = releases
            .Where(r => r.Version <= upTo && (after is null || r.Version > after))
            .OrderByDescending(r => r.Version);

        return released
            .Take(after is null ? 1 : int.MaxValue)
            .Where(r => r.Entries.Count > 0)
            .Take(max)
            .ToList();
    }
}
