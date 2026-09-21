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
/// The watcher fires far more often than there is useful work, so writes are coalesced
/// into one ingest pass per <see cref="Debounce"/> window.
/// </para>
/// </summary>
public sealed class LogWatchService(
    TrackerOptions options,
    TrackerDb db,
    TrackerState state,
    WikiNameService names,
    ILogger<LogWatchService> log) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A safety net for the cases FileSystemWatcher misses: network paths, and the game
    /// rotating Game.log while we are not looking.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Serialises ingest passes so a "rebuild now" and the background loop never
    /// hit the store at the same time.</summary>
    private readonly SemaphoreSlim _ingestLock = new(1, 1);
    private FileSystemWatcher? _watcher;

    /// <summary>Nudges the service to ingest now — used by the tray's "Rescan" command.</summary>
    public void RequestScan()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a pass is already pending */ }
        catch (ObjectDisposedException) { /* shutting down; a watcher event raced Dispose */ }
    }

    /// <summary>Runs one ingest pass and returns only when it has finished, so the caller
    /// can show a rebuild-complete state.</summary>
    public Task RebuildNowAsync() => Task.Run(Ingest);

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
    public Task ResetAndRebuildAsync() => Task.Run(() =>
    {
        _ingestLock.Wait();
        try
        {
            db.ResetIngest();
            IngestLocked();
        }
        finally
        {
            _ingestLock.Release();
        }
    });

    /// <summary>
    /// Re-points the watcher after Settings changes the log directory, and scans the new
    /// location right away rather than waiting for the next sweep.
    /// </summary>
    public void RestartWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        StartWatching();
        RequestScan();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Everything before the first await runs inline on the host's startup thread, and
        // the first pass is a full backfill over every rotated log. Without this the tray
        // icon, Kestrel and the browser launch all wait on it.
        await Task.Yield();

        Ingest();
        StartWatching();

        // Without the catalogue every item shows as a raw class name, which is useless as
        // a search term. Refresh it every launch, not just the first, so items the wiki
        // added since the last run (and the catalogue's own edits) show up without the
        // user having to remember to hit the manual refresh.
        _ = RefreshCatalogueAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Either the watcher woke us or the sweep timer expired; both mean "look again".
                await _wake.WaitAsync(SweepInterval, ct);
                await Task.Delay(Debounce, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Ingest();
        }
    }

    private async Task RefreshCatalogueAsync(CancellationToken ct)
    {
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
        }
        catch (Exception ex)
        {
            // Names are a nicety — the tracker still works with raw class names, and the
            // tray menu offers a manual retry.
            log.LogWarning(ex, "Could not download item names");
        }
    }

    private void StartWatching()
    {
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

        _watcher.Changed += (_, _) => RequestScan();
        _watcher.Created += (_, _) => RequestScan();
    }

    private void Ingest()
    {
        _ingestLock.Wait();
        try
        {
            IngestLocked();
        }
        finally
        {
            _ingestLock.Release();
        }
    }

    /// <summary>One ingest pass. The caller must already hold <see cref="_ingestLock"/>.</summary>
    private void IngestLocked()
    {
        try
        {
            var stats = new LogIngestor(db, options.LogDir, options.InceptionDate).IngestAll();

            // Re-resolving is cheap, but pushing a no-op update to every open page is noise.
            if (stats.MovesInserted > 0 ||
                stats.ContainersIdentified > 0 ||
                state.Current.BuiltAt == DateTimeOffset.MinValue)
            {
                state.Rebuild();
                log.LogInformation(
                    "Ingested {Moves} moves from {Files} file(s)", stats.MovesInserted, stats.FilesParsed);
            }
        }
        catch (IOException ex)
        {
            // The game holds Game.log open and rotates it out from under us; the next
            // pass picks up where this one stopped.
            log.LogDebug(ex, "Log temporarily unreadable");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ingest pass failed");
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
