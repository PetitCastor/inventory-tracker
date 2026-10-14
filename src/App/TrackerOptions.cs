using InventoryTracker.Data;
using InventoryTracker.Ingest;

namespace InventoryTracker.App;

/// <summary>Where the tray app reads from and writes to, and which port the UI listens on.</summary>
public sealed class TrackerOptions
{
    private readonly object _logSourceGate = new();
    private readonly SemaphoreSlim _applyLogSourceGate = new(1, 1);
    private string _logDir = LogFileLocator.DefaultLogDir;
    private DateTimeOffset? _inceptionDate;

    public string LogDir
    {
        get { lock (_logSourceGate) return _logDir; }
        set { lock (_logSourceGate) _logDir = value; }
    }
    public string DatabasePath { get; set; } = TrackerDb.DefaultPath;

    /// <summary>Fixed so the bookmarked URL keeps working across restarts.</summary>
    public int Port { get; set; } = AppConfig.DefaultPort;

    /// <summary>The wipe date; see <see cref="AppConfig.InceptionDate"/>. Null reads the whole history.</summary>
    public DateTimeOffset? InceptionDate
    {
        get { lock (_logSourceGate) return _inceptionDate; }
        set { lock (_logSourceGate) _inceptionDate = value; }
    }

    internal (string LogDir, DateTimeOffset? InceptionDate) CaptureLogSource(Action? afterLogDirCaptured = null)
    {
        lock (_logSourceGate)
        {
            var logDir = _logDir;
            afterLogDirCaptured?.Invoke();
            return (logDir, _inceptionDate);
        }
    }

    internal void SetLogSource(
        string logDir, DateTimeOffset? inceptionDate, Action? afterContention = null)
    {
        if (!Monitor.TryEnter(_logSourceGate))
        {
            afterContention?.Invoke();
            lock (_logSourceGate)
            {
                _logDir = logDir;
                _inceptionDate = inceptionDate;
            }

            return;
        }

        try
        {
            _logDir = logDir;
            _inceptionDate = inceptionDate;
        }
        finally
        {
            Monitor.Exit(_logSourceGate);
        }
    }

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
        string logDir, DateOnly? inceptionDate, LogWatchService watcher, bool markSetupComplete = false,
        string? configPath = null)
        => await ApplyLogSourceAsync(logDir, inceptionDate, watcher, markSetupComplete, configPath, hooks: null);

    internal async Task ApplyLogSourceAsync(
        string logDir, DateOnly? inceptionDate, LogWatchService watcher, bool markSetupComplete,
        string? configPath, TrackerOptionsApplyHooks? hooks)
    {
        hooks?.BeforeGate?.Invoke();
        if (!_applyLogSourceGate.Wait(0))
        {
            hooks?.OnApplyContended?.Invoke();
            await _applyLogSourceGate.WaitAsync();
        }
        try
        {
            if (string.IsNullOrWhiteSpace(logDir) || !Directory.Exists(logDir))
                throw new DirectoryNotFoundException($"Log directory does not exist: {logDir}");

            var config = AppConfig.Load(configPath);
            var newDate = inceptionDate is { } d
                ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : (DateTimeOffset?)null;

            // Against what the running watcher has been using, read before it is overwritten — not
            // against config.json, whose LogDir can still be unset on a fresh install and would
            // restart the watcher for a folder that never changed.
            var currentSource = CaptureLogSource();
            var logDirChanged = !string.Equals(currentSource.LogDir, logDir, StringComparison.OrdinalIgnoreCase);
            var sourceUnchanged = !logDirChanged && currentSource.InceptionDate == newDate;

            config.LogDir = logDir;
            config.InceptionDate = newDate;
            if (markSetupComplete) config.SetupComplete = true;
            config.Save(configPath);
            hooks?.AfterConfigurationSaved?.Invoke();

            SetLogSource(config.LogDir ?? logDir, config.InceptionDate);
            hooks?.AfterSourceApplied?.Invoke();
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
            if (hooks?.BeforeResetAsync is { } beforeReset) await beforeReset();
            await watcher.ResetAndRebuildAsync();
        }
        finally
        {
            _applyLogSourceGate.Release();
        }
    }
}

internal sealed class TrackerOptionsApplyHooks
{
    public Action? BeforeGate { get; init; }
    public Action? OnApplyContended { get; init; }
    public Action? AfterConfigurationSaved { get; init; }
    public Action? AfterSourceApplied { get; init; }
    public Func<Task>? BeforeResetAsync { get; init; }
}
