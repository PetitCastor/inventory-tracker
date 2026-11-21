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

    private DateTimeOffset? _resetNoticeDismissed;

    /// <summary>See <see cref="AppConfig.ResetNoticeDismissed"/>.</summary>
    public DateTimeOffset? ResetNoticeDismissed
    {
        get { lock (_logSourceGate) return _resetNoticeDismissed; }
        set { lock (_logSourceGate) _resetNoticeDismissed = value; }
    }

    /// <summary>True once the user has closed the popup for this reset, or for a later one.</summary>
    public bool HasDismissedReset(DateTimeOffset resetAt) => ResetNoticeDismissed >= resetAt;

    /// <summary>
    /// Closes the character-reset popup for good: at once for this run, then in config.json.
    /// <para>
    /// The save waits behind any log-source change, which loads, edits and saves the same file:
    /// two unsynchronised read-modify-writes could put back the wipe date the user just changed.
    /// </para>
    /// </summary>
    public async Task DismissResetNoticeAsync(
        DateTimeOffset resetAt, string? configPath = null, CancellationToken cancellationToken = default)
    {
        ResetNoticeDismissed = resetAt;
        await SaveConfigAsync(config => config.ResetNoticeDismissed = resetAt, configPath, cancellationToken);
    }

    /// <summary>
    /// Brings the wipe date forward to the day of a character reset: at once for this run, then
    /// in config.json. A reset starts the tracker over, so it is a wipe date the tracker can find
    /// on its own. A date already on or after that day is left as it is.
    /// <para>
    /// Nothing is re-read: the replay already cleared everything at the reset, and moving the
    /// date only makes a later rebuild skip what was cleared. It waits behind any log-source
    /// change, like <see cref="DismissResetNoticeAsync"/>, and checks the date again once it holds
    /// the gate.
    /// </para>
    /// </summary>
    /// <returns>True when the wipe date moved.</returns>
    public async Task<bool> FollowCharacterResetAsync(
        DateTimeOffset resetAt, string? configPath = null, CancellationToken cancellationToken = default)
    {
        var day = WipeDateFor(resetAt);
        await _applyLogSourceGate.WaitAsync(cancellationToken);
        try
        {
            var (logDir, current) = CaptureLogSource();
            if (current >= day) return false;

            var config = AppConfig.Load(configPath);
            config.InceptionDate = day;
            config.Save(configPath);
            SetLogSource(logDir, day);
            return true;
        }
        finally
        {
            _applyLogSourceGate.Release();
        }
    }

    /// <summary>
    /// The wipe date a reset at <paramref name="resetAt"/> calls for: the start of its UTC day,
    /// as Settings stores a date. Anything logged earlier that day is still read, and the reset
    /// clears it in the replay.
    /// </summary>
    public static DateTimeOffset WipeDateFor(DateTimeOffset resetAt) =>
        new(resetAt.UtcDateTime.Date, TimeSpan.Zero);

    private AppTheme _theme;

    /// <summary>See <see cref="AppConfig.Theme"/>.</summary>
    public AppTheme Theme
    {
        get { lock (_logSourceGate) return _theme; }
        set { lock (_logSourceGate) _theme = value; }
    }

    /// <summary>Raised after <see cref="SetThemeAsync"/> changes the theme, on the caller's thread.
    /// The app window follows it so its title bar matches the page.</summary>
    public event Action<AppTheme>? ThemeChanged;

    /// <summary>Applies a theme at once for this run, then saves it in config.json, behind the
    /// same gate as <see cref="DismissResetNoticeAsync"/>.</summary>
    public async Task SetThemeAsync(
        AppTheme theme, string? configPath = null, CancellationToken cancellationToken = default)
    {
        Theme = theme;
        ThemeChanged?.Invoke(theme);
        await SaveConfigAsync(config => config.Theme = theme, configPath, cancellationToken);
    }

    private string? _lastSeenVersion;

    /// <summary>See <see cref="AppConfig.LastSeenVersion"/>.</summary>
    public string? LastSeenVersion
    {
        get { lock (_logSourceGate) return _lastSeenVersion; }
        set { lock (_logSourceGate) _lastSeenVersion = value; }
    }

    /// <summary>Records <paramref name="version"/> as announced: at once for this run, then in config.json.</summary>
    public async Task MarkVersionSeenAsync(
        string version, string? configPath = null, CancellationToken cancellationToken = default)
    {
        LastSeenVersion = version;
        await SaveConfigAsync(config => config.LastSeenVersion = version, configPath, cancellationToken);
    }

    // Load, edit, save under the log-source gate: ApplyLogSourceAsync rewrites the same file, and
    // two unsynchronised read-modify-writes would each put back what the other just changed.
    private async Task SaveConfigAsync(
        Action<AppConfig> edit, string? configPath, CancellationToken cancellationToken)
    {
        await _applyLogSourceGate.WaitAsync(cancellationToken);
        try
        {
            var config = AppConfig.Load(configPath);
            edit(config);
            config.Save(configPath);
        }
        finally
        {
            _applyLogSourceGate.Release();
        }
    }

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
            ResetNoticeDismissed = config.ResetNoticeDismissed,
            Theme = config.Theme ?? AppTheme.System,
            LastSeenVersion = config.LastSeenVersion,
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
        string? configPath = null, CancellationToken cancellationToken = default)
        => await ApplyLogSourceAsync(logDir, inceptionDate, watcher, markSetupComplete, configPath,
            hooks: null, cancellationToken: cancellationToken);

    internal async Task ApplyLogSourceAsync(
        string logDir, DateOnly? inceptionDate, LogWatchService watcher, bool markSetupComplete,
        string? configPath, TrackerOptionsApplyHooks? hooks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        hooks?.BeforeGate?.Invoke();
        if (!_applyLogSourceGate.Wait(0))
        {
            hooks?.OnApplyContended?.Invoke();
            await _applyLogSourceGate.WaitAsync(cancellationToken);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                await watcher.RebuildNowAsync(cancellationToken);
                return;
            }

            // The wipe and the re-ingest go together under the watcher's ingest lock; doing the
            // reset here would race a background pass already in flight. The config and source
            // are already committed, so finish this critical operation before reporting shutdown.
            if (hooks?.BeforeResetAsync is { } beforeReset) await beforeReset();
            await watcher.ResetAndRebuildAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _applyLogSourceGate.Release();
        }
    }
}

internal static class FormOperationErrors
{
    public static bool TryGetMessage(Exception exception, out string message)
    {
        if (exception is IOException or UnauthorizedAccessException or HttpRequestException or
            InvalidDataException or TimeoutException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            message = exception.Message;
            return true;
        }

        message = "";
        return false;
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
