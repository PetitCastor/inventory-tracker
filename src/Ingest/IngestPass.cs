namespace InventoryTracker.Ingest;

/// <summary>
/// The outcome of one ingest pass, kept so Settings can show when the logs were last read and
/// what came of it. Without it, "the watcher never ran" and "it ran and found nothing" look
/// the same: an empty page.
/// </summary>
/// <param name="Rebuilt">Whether the pass published a new snapshot to the UI.</param>
/// <param name="Error">Why the pass stopped early, or null when it finished.</param>
public sealed record IngestPass(
    DateTimeOffset At,
    IngestTrigger Trigger,
    TimeSpan Duration,
    int LinesRead,
    int MovesInserted,
    bool Rebuilt,
    string? Error)
{
    /// <summary>File-level read failures; the pass continued and will retry them.</summary>
    public string? Incomplete { get; init; }
}
