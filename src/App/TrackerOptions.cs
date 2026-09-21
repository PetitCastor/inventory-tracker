using InventoryTracker.Data;
using InventoryTracker.Ingest;

namespace InventoryTracker.App;

/// <summary>Where the tray app reads from and writes to, and which port the UI listens on.</summary>
public sealed class TrackerOptions
{
    public string LogDir { get; set; } = LogFileLocator.DefaultLogDir;
    public string DatabasePath { get; set; } = TrackerDb.DefaultPath;

    /// <summary>Fixed so the bookmarked URL keeps working across restarts.</summary>
    public int Port { get; set; } = AppConfig.DefaultPort;

    /// <summary>Everything logged before this is ignored. Null means no inception date is set.</summary>
    public DateTimeOffset? InceptionDate { get; set; }

    /// <summary>False until the first-run setup wizard has been completed; see <see cref="AppConfig.SetupComplete"/>.</summary>
    public bool SetupComplete { get; set; }

    public string Url => $"http://localhost:{Port}";

    /// <summary>
    /// Reads config.json (see <see cref="AppConfig"/>) — the only place these settings
    /// come from. Edit that file and relaunch to change log dir, database path, port,
    /// or inception date.
    /// </summary>
    public static TrackerOptions Load()
    {
        var config = AppConfig.Load();

        return new TrackerOptions
        {
            LogDir = config.LogDir ?? LogFileLocator.DefaultLogDir,
            DatabasePath = config.DatabasePath ?? TrackerDb.DefaultPath,
            Port = config.Port ?? AppConfig.DefaultPort,
            InceptionDate = config.InceptionDate,
            SetupComplete = config.SetupComplete,
        };
    }

    /// <summary>
    /// Persists a new log directory and inception date to config.json, applies them here,
    /// restarts the watcher if the directory changed, then wipes and re-ingests under its
    /// lock. Setup and Settings both drive a log-source change through this single path so
    /// the two forms cannot answer "what happens when you change it" differently.
    /// </summary>
    public async Task ApplyLogSourceAsync(
        string logDir, DateOnly? inceptionDate, LogWatchService watcher, bool markSetupComplete = false)
    {
        var config = AppConfig.Load();
        var logDirChanged = !string.Equals(config.LogDir, logDir, StringComparison.OrdinalIgnoreCase);

        config.LogDir = logDir;
        config.InceptionDate = inceptionDate is { } d
            ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
        if (markSetupComplete) config.SetupComplete = true;
        config.Save();

        LogDir = config.LogDir ?? logDir;
        InceptionDate = config.InceptionDate;
        if (markSetupComplete) SetupComplete = true;

        if (logDirChanged) watcher.RestartWatching();

        // The wipe and the re-ingest go together under the watcher's ingest lock; doing the
        // reset here would race a background pass already in flight.
        await watcher.ResetAndRebuildAsync();
    }
}
