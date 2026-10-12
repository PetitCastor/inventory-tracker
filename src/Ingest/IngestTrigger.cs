namespace InventoryTracker.Ingest;

/// <summary>What started an ingest pass. Shown in Settings, so a pass that never ran is
/// told apart from one that ran and found nothing.</summary>
public enum IngestTrigger
{
    /// <summary>The backfill when the app starts.</summary>
    Startup,

    /// <summary>FileSystemWatcher saw Game.log change.</summary>
    Watcher,

    /// <summary>The length poll saw Game.log grow, shrink or appear.</summary>
    Poll,

    /// <summary>The slow safety-net timer, with no change seen.</summary>
    Sweep,

    /// <summary>"Rescan logs now", or something outside the logs that place names depend on.</summary>
    Manual,

    /// <summary>The wipe and re-read after the log source or wipe date changed.</summary>
    Reset,
}
