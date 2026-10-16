using InventoryTracker.App;
using InventoryTracker.Data;
using InventoryTracker.Naming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Ingest;

/// <summary>
/// Keeps the store current: backfills every rotated log on start, then follows the live
/// Game.log as the game writes to it.
/// <para>
/// Three things start a pass. FileSystemWatcher is the fast path, but it reads the directory
/// entry, which NTFS updates only lazily while the game holds the file open — it can stay
/// silent for minutes. The length poll every <see cref="PollInterval"/> is what actually
/// follows a running game; see <see cref="LogFileLocator.LiveLength"/>. The sweep every
/// <see cref="SweepInterval"/> covers anything else that changed with Game.log unchanged.
/// </para>
/// <para>
/// The watcher fires far more often than there is useful work, so its events are coalesced
/// into one ingest pass per <see cref="Debounce"/> window.
/// </para>
/// </summary>
public sealed class LogWatchService(
    TrackerOptions options,
    TrackerDb db,
    TrackerState state,
    WikiNameService names,
    PlaceNameService places,
    ILogger<LogWatchService> log) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often Game.log's length is checked. Opening a handle and reading its length is
    /// cheap, and a pass only follows when the length moved.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>A pass even when nothing was seen to change, in case the poll and the
    /// watcher both missed it.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    /// <summary>Failed passes in a row before a routine IOException is worth a warning:
    /// one or two are the game rotating Game.log, more means the file stays unreadable.</summary>
    private const int FailuresBeforeWarning = 3;

    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Failed passes retried on the next poll tick rather than at the sweep. Past
    /// this a failure that keeps happening is left to the sweep, not retried every 2 s.</summary>
    private const int QuickRetries = 5;

    /// <summary>
    /// 1 while a manual scan is waiting for the loop. Sticky rather than a last-writer-wins
    /// label: a manual pass forces a rebuild, and a watcher event landing before the loop
    /// wakes must not turn it back into an ordinary pass.
    /// </summary>
    private int _manualPending;

    /// <summary>Game.log's length when the last pass started, -1 when there was none, or
    /// <see cref="RetryNow"/>. The poll compares against it. Written by whichever thread runs
    /// the pass.</summary>
    private long _seenLength = -1;

    /// <summary>A length no file has, so the next poll tick runs a pass.</summary>
    private const long RetryNow = -2;

    /// <summary>UTC ticks of the last pass's start, for the sweep.</summary>
    private long _lastPassTicks;

    /// <summary>Set when a pass changed the store but could not publish a snapshot, so the
    /// next pass publishes even if it reads nothing new. Guarded by <see cref="_ingestLock"/>.</summary>
    private bool _rebuildOwed;

    /// <summary>Failed passes in a row. Guarded by <see cref="_ingestLock"/>.</summary>
    private int _consecutiveFailures;

    private readonly Lock _statusGate = new();
    private IngestPass? _lastPass;
    private DateTimeOffset? _lastMovesAt;

    /// <summary>Serialises ingest passes so a "rebuild now" and the background loop never
    /// hit the store at the same time.</summary>
    private readonly SemaphoreSlim _ingestLock = new(1, 1);
    private FileSystemWatcher? _watcher;

    /// <summary>
    /// Kept across passes because it carries the live file's parse state from one to the
    /// next — see <see cref="LogIngestor"/>. Guarded by <see cref="_ingestLock"/>.
    /// </summary>
    private LogIngestor? _ingestor;

    /// <summary>
    /// Set when something place names are resolved from changed outside the logs — the wiki's
    /// location list, or the app just started and the game's text may have been patched — so
    /// the next pass resolves even if it read nothing new. Starts set for that reason.
    /// </summary>
    private volatile bool _placesStale = true;

    /// <summary>The most recent pass, or null before the first one finishes.</summary>
    public IngestPass? LastPass
    {
        get { lock (_statusGate) return _lastPass; }
    }

    /// <summary>When a pass last found new moves, or null if none has since launch.</summary>
    public DateTimeOffset? LastMovesAt
    {
        get { lock (_statusGate) return _lastMovesAt; }
    }

    /// <summary>Raised after every pass, including the ones that found nothing and so
    /// published no snapshot — Settings shows when the logs were last read.</summary>
    public event Action? PassCompleted;

    /// <summary>Resolves place names again on the next pass, then nudges one.</summary>
    public void RequestPlaceResolution()
    {
        // Not a manual scan: that would force a rebuild, and the pass rebuilds anyway when a
        // name actually changes.
        _placesStale = true;
        Wake();
    }

    /// <summary>Nudges the service to ingest now — used by the tray's "Rescan" command.</summary>
    public void RequestScan()
    {
        Interlocked.Exchange(ref _manualPending, 1);
        Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a pass is already pending */ }
        catch (ObjectDisposedException) { /* shutting down; a watcher event raced Dispose */ }
    }

    /// <summary>Runs one ingest pass and returns only when it has finished, so the caller
    /// can show a rebuild-complete state.</summary>
    public Task RebuildNowAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            Ingest(IngestTrigger.Manual, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);

    /// <summary>One pass as the loop would run it for <paramref name="trigger"/>, for tests:
    /// the unforced triggers are the ones whose rebuild decisions need checking.</summary>
    internal Task RunPassAsync(IngestTrigger trigger) => Task.Run(() => Ingest(trigger));

    /// <summary>
    /// Raised outside the ingest lock, and shielded: a subscriber that throws must not stop
    /// the loop or fail a reset whose wipe and re-read already succeeded, and a slow one must
    /// not hold up the next pass.
    /// </summary>
    private void RaisePassCompleted()
    {
        try
        {
            PassCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "A PassCompleted subscriber failed");
        }
    }

    /// <summary>
    /// Discards everything derived from the logs and re-ingests from scratch, as one
    /// operation under the ingest lock.
    /// <para>
    /// The wipe must not be done by the caller: a background pass triggered by the watcher
    /// or the sweep timer can be mid-transaction, and its commit would land after the delete
    /// — leaving rows behind plus a session watermark pointing past data that no longer
    /// exists, so those lines would never be re-read.
    /// </para>
    /// </summary>
    public Task ResetAndRebuildAsync(CancellationToken cancellationToken = default) =>
        ResetAndRebuildAsync(cancellationToken, afterLockAcquired: null, afterResetStarted: null);

    internal Task ResetAndRebuildAsync(
        CancellationToken cancellationToken,
        Func<Task>? afterLockAcquired,
        Func<Task>? afterResetStarted = null) => Task.Run(async () =>
    {
        // Cancellation can prevent a queued reset from starting. Once the lock is acquired,
        // reset and re-ingest are one critical operation: finish it before reporting cancellation.
        try
        {
            await _ingestLock.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (afterLockAcquired is not null) await afterLockAcquired();
            cancellationToken.ThrowIfCancellationRequested();
            db.ResetIngest();
            if (afterResetStarted is not null) await afterResetStarted();

            // The wipe alone changed what the UI should show; if the re-read below fails,
            // the next pass still owes the snapshot.
            _rebuildOwed = true;

            // Session ids restart once the table is emptied, so carried state could
            // otherwise land on an unrelated new session with the same id.
            _ingestor = null;
            IngestLocked(IngestTrigger.Reset);
        }
        finally
        {
            try { _ingestLock.Release(); }
            catch (ObjectDisposedException) { }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RaisePassCompleted();
    }, cancellationToken);

    /// <summary>
    /// Re-points the watcher after Settings changes the log directory. No scan of its own:
    /// the only caller resets and re-reads right after, and a pass queued here would only
    /// run a second forced rebuild behind it.
    /// </summary>
    public void RestartWatching() => StartWatching();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Everything before the first await runs inline on the host's startup thread, and
        // the first pass is a full backfill over every rotated log. Without this the tray
        // icon, Kestrel and the browser launch all wait on it.
        await Task.Yield();

        Ingest(IngestTrigger.Startup, ct);
        if (ct.IsCancellationRequested) return;

        StartWatching();

        // Without the catalogue every item shows as a raw class name, which is useless as
        // a search term. Refresh it every launch, not just the first, so items the wiki
        // added since the last run (and the catalogue's own edits) show up without the
        // user having to remember to hit the manual refresh.
        var startupRefreshes = new[]
        {
            RefreshCatalogueAsync(ct),
            RefreshPlacesAsync(ct),
        };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                IngestTrigger trigger;
                try
                {
                    if (await _wake.WaitAsync(PollInterval, ct))
                    {
                        await Task.Delay(Debounce, ct);
                        trigger = Interlocked.Exchange(ref _manualPending, 0) == 1 ? IngestTrigger.Manual
                            : _placesStale ? IngestTrigger.Places
                            : IngestTrigger.Watcher;
                    }
                    else if (await ProbeLiveLengthAsync(ct) != Interlocked.Read(ref _seenLength))
                    {
                        trigger = IngestTrigger.Poll;
                    }
                    else if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPassTicks) >= SweepInterval.Ticks)
                    {
                        trigger = IngestTrigger.Sweep;
                    }
                    else
                    {
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    Ingest(trigger, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The pass guards itself; this is for whatever slips past it. An exception out
                    // of ExecuteAsync stops the host, which would take the whole tracker down.
                    log.LogError(ex, "Watch loop iteration failed");
                }
            }
        }
        finally
        {
            await Task.WhenAll(startupRefreshes);
        }
    }

    /// <summary>How long one length probe may take before the tick treats it as no change.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Game.log's length, or -1. Off the loop thread with a timeout: on an unreachable network
    /// share the open blocks for the network timeout, and the loop would stop answering wakes
    /// and shutdown meanwhile. A probe that times out reads as unchanged; the sweep still runs.
    /// </summary>
    private async Task<long> ProbeLiveLengthAsync(CancellationToken ct)
    {
        // A probe abandoned by the timeout below still holds a pool thread until the open
        // gives up. Never stack another on top of it: on a share that hangs for good, one
        // stuck thread is the cost, not one more every tick.
        if (_probe is { IsCompleted: false }) return Interlocked.Read(ref _seenLength);

        try
        {
            var dir = options.LogDir;
            _probe = Task.Run(() => LogFileLocator.LiveLength(dir) ?? -1, ct);
            return await _probe.WaitAsync(ProbeTimeout, ct);
        }
        catch (TimeoutException)
        {
            return Interlocked.Read(ref _seenLength);
        }
    }

    /// <summary>The last length probe, kept so a hung one is not joined by more.
    /// Only the loop touches it.</summary>
    private Task<long>? _probe;

    private async Task RefreshCatalogueAsync(CancellationToken ct)
    {
        var previousRefresh = names.LastRefresh();
        try
        {
            log.LogInformation("Downloading item catalogue from star-citizen.wiki");
            var count = await names.RefreshAsync(ct: ct);
            log.LogInformation("Cached {Count} item names", count);
            state.Rebuild();
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-download; the next launch retries.
            if (names.LastRefresh() != previousRefresh) state.Rebuild();
        }
        catch (Exception ex)
        {
            // Names are a nicety — the tracker still works with raw class names, and the
            // tray menu offers a manual retry.
            if (names.LastRefresh() != previousRefresh) state.Rebuild();
            log.LogWarning(ex, "Could not download item names");
        }
    }

    /// <summary>
    /// The wiki's location list is what a station's name is checked against; until it is
    /// here, names backed by a single source stay unconfirmed. A pass follows, so places
    /// waiting on it are resolved again right away.
    /// </summary>
    private async Task RefreshPlacesAsync(CancellationToken ct)
    {
        try
        {
            log.LogInformation("Downloading location list from star-citizen.wiki");
            var count = await places.RefreshAsync(ct: ct);
            log.LogInformation("Cached {Count} wiki locations", count);
            RequestPlaceResolution();
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-download; the next launch retries.
        }
        catch (Exception ex)
        {
            // Names wait for the list, and the player can still confirm them by hand.
            log.LogWarning(ex, "Could not download the wiki's location list");
        }
    }

    /// <summary>
    /// Replaces any watcher already running. Setup can restart watching while the startup
    /// pass is still going, and ExecuteAsync starts one when that pass ends: without the
    /// dispose the first is orphaned, keeps waking the loop, and outlives a later folder change.
    /// </summary>
    private void StartWatching()
    {
        lock (_watcherGate)
        {
            _watcher?.Dispose();
            _watcher = null;

            if (!Directory.Exists(options.LogDir))
            {
                log.LogWarning("Log directory {Dir} does not exist; watching disabled", options.LogDir);
                return;
            }

            _watcher = new FileSystemWatcher(options.LogDir, "Game.log")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += (_, _) => Wake();
            _watcher.Created += (_, _) => Wake();
        }
    }

    /// <summary>The loop and a Settings change can both start watching.</summary>
    private readonly Lock _watcherGate = new();

    /// <summary>
    /// <paramref name="ct"/> is the host's stop token: shutdown waits for the pass in flight, so
    /// it has to be able to cut a long backfill short. The next launch resumes from the watermark.
    /// </summary>
    private void Ingest(IngestTrigger trigger, CancellationToken ct = default)
    {
        try
        {
            _ingestLock.Wait(ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            IngestLocked(trigger, ct);
        }
        finally
        {
            // The host disposes this service once its shutdown timeout runs out, even if a
            // pass is still winding down; the lock may be gone by the time it finishes.
            try { _ingestLock.Release(); }
            catch (ObjectDisposedException) { }
        }

        RaisePassCompleted();
    }

    /// <summary>
    /// One ingest pass. The caller must already hold <see cref="_ingestLock"/>.
    /// <para>
    /// A reset or a manual rescan always publishes a snapshot. A reset in particular can
    /// wipe the store and re-read nothing — a wipe date after every log line — and without
    /// the forced rebuild the UI would keep showing the rows that were just deleted.
    /// </para>
    /// </summary>
    private void IngestLocked(IngestTrigger trigger, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _lastPassTicks, started.UtcTicks);

        // Sampled before reading, so a write that lands mid-pass still looks new to the poll.
        Interlocked.Exchange(ref _seenLength, LogFileLocator.LiveLength(options.LogDir) ?? -1);

        IngestStats? stats = null;
        var rebuilt = false;
        string? error = null;
        string? incomplete = null;
        var incompleteWasPending = LastPass?.Incomplete is not null;

        // Decided before reading: a forced pass that fails part-way still owes its snapshot.
        if (trigger is IngestTrigger.Reset or IngestTrigger.Manual) _rebuildOwed = true;

        try
        {
            // A settings change swaps the source out from under the carried state; start over.
            if (_ingestor is null
                || _ingestor.LogDir != options.LogDir
                || _ingestor.FromDate != options.InceptionDate)
            {
                _ingestor = new LogIngestor(db, options.LogDir, options.InceptionDate);
            }

            stats = _ingestor.IngestAll(ct);
            if (stats.FileErrors.Count > 0)
            {
                incomplete = string.Join("; ", stats.FileErrors);
                log.LogWarning("Could not read {Count} log file(s); they will be retried: {Files}",
                    stats.FileErrors.Count, incomplete);
            }
            var renamed = ResolvePlaces(stats);

            // Re-resolving is cheap, but pushing a no-op update to every open page is noise.
            // An UnrecognisedMove counts too: it means the game's log grammar just drifted,
            // and the health snapshot the UI reads needs to reflect that as soon as possible
            // rather than waiting for some unrelated change to trigger the next rebuild.
            if (stats.ChangedHoldings || stats.FileErrors.Count > 0 || incompleteWasPending || renamed > 0 ||
                state.Current.BuiltAt == DateTimeOffset.MinValue || (stats.LinesRead > 0 && BannerStale()))
            {
                _rebuildOwed = true;
            }

            // Before the rebuild: the moves are committed whether or not it succeeds, and the
            // diagnostics matter most exactly when it does not.
            if (stats.MovesInserted > 0)
            {
                lock (_statusGate) _lastMovesAt = started;
                log.LogInformation(
                    "Ingested {Moves} moves from {Files} file(s) ({Trigger})",
                    stats.MovesInserted, stats.FilesParsed, trigger);
            }
            else if (trigger is IngestTrigger.Startup or IngestTrigger.Reset)
            {
                log.LogInformation(
                    "{Trigger} pass read {Lines} line(s) from {Files} file(s); no new moves",
                    trigger, stats.LinesRead, stats.FilesSeen);
            }

            // Owed across passes: the store is already committed, so a rebuild that throws
            // here would otherwise leave the UI behind until some later pass happens to read
            // something new.
            if (_rebuildOwed)
            {
                state.Rebuild();
                _rebuildOwed = false;
                rebuilt = true;
            }

            if (stats.UnrecognisedMoves > 0)
            {
                log.LogWarning(
                    "{Count} log line(s) looked like inventory move requests but matched none of the " +
                    "parser's patterns. The game's log format has probably changed; tracking is incomplete.",
                    stats.UnrecognisedMoves);
            }

            if (_consecutiveFailures >= FailuresBeforeWarning)
            {
                log.LogInformation("Log reading recovered after {Count} failed passes", _consecutiveFailures);
            }

            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-pass; the next launch picks up from the watermark.
            return;
        }
        catch (IOException ex)
        {
            // The game holds Game.log open and rotates it out from under us; the next
            // pass picks up where this one stopped. Only a run of these is a problem.
            // IngestAll commits file by file, so a rotated backup may already be in: the
            // snapshot is owed either way.
            error = ex.Message;
            _rebuildOwed = true;
            if (++_consecutiveFailures == FailuresBeforeWarning)
            {
                log.LogWarning(ex, "Logs unreadable for {Count} passes in a row", _consecutiveFailures);
            }
            else
            {
                log.LogDebug(ex, "Log temporarily unreadable");
            }
        }
        catch (Exception ex)
        {
            // Every pass would fail the same way, so only the first of a run is logged.
            error = ex.Message;
            _rebuildOwed = true;
            if (++_consecutiveFailures == 1) log.LogError(ex, "Ingest pass failed");
        }

        // Retried on the next poll tick rather than at the sweep, which with the game idle
        // is two minutes away — but only for a few passes, so a failure that persists does
        // not turn into a pass every 2 s.
        if (error is not null && _consecutiveFailures <= QuickRetries)
        {
            Interlocked.Exchange(ref _seenLength, RetryNow);
        }

        var pass = new IngestPass(
            started, trigger, DateTimeOffset.UtcNow - started,
            stats?.LinesRead ?? 0, stats?.MovesInserted ?? 0, rebuilt, error)
        {
            Incomplete = incomplete,
        };
        lock (_statusGate) _lastPass = pass;
    }

    /// <summary>
    /// Whether Browse's health banner would now read differently from the published snapshot.
    /// A pass that reads only non-inventory lines — the game logging in and loading — changes
    /// none of the holdings, but can turn "the game has not written anything yet" or "no logs
    /// found" false. Only the facts the banners branch on are compared: the newest line's time
    /// moves on every pass while the game writes, and rebuilding for it would mean a rebuild
    /// every two seconds.
    /// </summary>
    private bool BannerStale()
    {
        var shown = state.Current.Health;
        var now = IngestHealth.Load(db, options.LogDir, options.InceptionDate);

        return now.LogFiles != shown.LogFiles
               || (now.FilesWithLinesSinceInception == 0) != (shown.FilesWithLinesSinceInception == 0)
               || (now.Moves == 0) != (shown.Moves == 0)
               || (now.NewestLogLine is null) != (shown.NewestLogLine is null);
    }

    /// <summary>
    /// Names a place seen for the first time, and looks again at any still waiting on
    /// confirmation — but only when there is something new to go on: the watcher fires on
    /// every write to Game.log, and most passes read no place evidence at all. A failure here
    /// costs the names, never the rebuild of what was just ingested.
    /// </summary>
    private int ResolvePlaces(IngestStats stats)
    {
        if (!_placesStale && stats.LocationsNamed == 0 && stats.LocationConflicts == 0 && stats.PlaceEvidence == 0)
        {
            return 0;
        }

        try
        {
            _placesStale = false;
            return places.ResolvePlaces();
        }
        catch (Exception ex)
        {
            // Some names may already be written. The retry only counts the ones it changes
            // itself, so owe the snapshot now or those would never reach the page.
            _placesStale = true;
            _rebuildOwed = true;
            log.LogWarning(ex, "Could not resolve place names");
            return 0;
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _wake.Dispose();
        _ingestLock.Dispose();
        base.Dispose();
    }
}
