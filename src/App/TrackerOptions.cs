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
}
