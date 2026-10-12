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

    /// <summary>The wipe date; see <see cref="AppConfig.InceptionDate"/>. Null reads the whole history.</summary>
    public DateTimeOffset? InceptionDate { get; set; }

    /// <summary>False until the first-run setup wizard has been completed; see <see cref="AppConfig.SetupComplete"/>.</summary>
    public bool SetupComplete { get; set; }

    public string Url => $"http://localhost:{Port}";

    /// <summary>
    /// Reads config.json (see <see cref="AppConfig"/>) — the only place these settings
    /// come from. Edit that file and relaunch to change log dir, database path, port,
    /// or wipe date.
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
    /// Persists a new log directory and wipe date to config.json, applies them here,
    /// restarts the watcher if the directory changed, then wipes and re-ingests under its
    /// lock. Setup and Settings both drive a log-source change through this single path so
    /// the two forms cannot answer "what happens when you change it" differently.
    /// </summary>
    public async Task ApplyLogSourceAsync(
        string logDir, DateOnly? inceptionDate, LogWatchService watcher, bool markSetupComplete = false)
    {
        var config = AppConfig.Load();
        var logDirChanged = !string.Equals(config.LogDir, logDir, StringComparison.OrdinalIgnoreCase);
        var newDate = inceptionDate is { } d
            ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        // What the running watcher has been ingesting with, compared before it is overwritten.
        var sourceUnchanged =
            string.Equals(LogDir, logDir, StringComparison.OrdinalIgnoreCase) && InceptionDate == newDate;

        config.LogDir = logDir;
        config.InceptionDate = newDate;
        if (markSetupComplete) config.SetupComplete = true;
        config.Save();

        LogDir = config.LogDir ?? logDir;
        InceptionDate = config.InceptionDate;
        if (markSetupComplete) SetupComplete = true;

        if (logDirChanged) watcher.RestartWatching();

        // First run with the folder and date the app started with: the startup pass is
        // already reading exactly that, and with no wipe date by default it is the whole
        // history. Wiping it to read it all again would double the wait behind the spinner;
        // a pass queued behind the startup one returns once that is done. Settings always
        // rebuilds — that button is an explicit request to.
        if (markSetupComplete && sourceUnchanged)
        {
            await watcher.RebuildNowAsync();
            return;
        }

        // The wipe and the re-ingest go together under the watcher's ingest lock; doing the
        // reset here would race a background pass already in flight.
        await watcher.ResetAndRebuildAsync();
    }
}
