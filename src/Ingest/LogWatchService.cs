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
    PlaceNameService places,
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

    /// <summary>Resolves place names again on the next pass, then nudges one.</summary>
    public void RequestPlaceResolution()
    {
        _placesStale = true;
        RequestScan();
    }

    /// <summary>Nudges the service to ingest now — used by the tray's "Rescan" command.</summary>
    public void RequestScan()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a pass is already pending */ }
        catch (ObjectDisposedException) { /* shutting down; a watcher event raced Dispose */ }
    }

    /// <summary>Runs one ingest pass and returns only when it has finished, so the caller
    /// can show a rebuild-complete state.</summary>
    public Task RebuildNowAsync() => Task.Run(() => Ingest());

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

            // Session ids restart once the table is emptied, so carried state could
            // otherwise land on an unrelated new session with the same id.
            _ingestor = null;
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

        Ingest(ct);
        StartWatching();

        // Without the catalogue every item shows as a raw class name, which is useless as
        // a search term. Refresh it every launch, not just the first, so items the wiki
        // added since the last run (and the catalogue's own edits) show up without the
        // user having to remember to hit the manual refresh.
        _ = RefreshCatalogueAsync(ct);
        _ = RefreshPlacesAsync(ct);

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

            Ingest(ct);
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

    /// <summary>
    /// <paramref name="ct"/> is the host's stop token: shutdown waits for the pass in flight, so
    /// it has to be able to cut a long backfill short. The next launch resumes from the watermark.
    /// </summary>
    private void Ingest(CancellationToken ct = default)
    {
        try
        {
            _ingestLock.Wait(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            IngestLocked(ct);
        }
        finally
        {
            _ingestLock.Release();
        }
    }

    /// <summary>One ingest pass. The caller must already hold <see cref="_ingestLock"/>.</summary>
    private void IngestLocked(CancellationToken ct = default)
    {
        try
        {
            // A settings change swaps the source out from under the carried state; start over.
            if (_ingestor is null
                || _ingestor.LogDir != options.LogDir
                || _ingestor.FromDate != options.InceptionDate)
            {
                _ingestor = new LogIngestor(db, options.LogDir, options.InceptionDate);
            }

            var stats = _ingestor.IngestAll(ct);
            var renamed = ResolvePlaces(stats);

            // Re-resolving is cheap, but pushing a no-op update to every open page is noise.
            // An UnrecognisedMove counts too: it means the game's log grammar just drifted,
            // and the health snapshot the UI reads needs to reflect that as soon as possible
            // rather than waiting for some unrelated change to trigger the next rebuild.
            if (stats.MovesInserted > 0 ||
                stats.ContainersIdentified > 0 ||
                stats.UnrecognisedMoves > 0 ||
                renamed > 0 ||
                state.Current.BuiltAt == DateTimeOffset.MinValue)
            {
                state.Rebuild();
                log.LogInformation(
                    "Ingested {Moves} moves from {Files} file(s)", stats.MovesInserted, stats.FilesParsed);
            }

            if (stats.UnrecognisedMoves > 0)
            {
                log.LogWarning(
                    "{Count} log line(s) looked like inventory move requests but matched none of the " +
                    "parser's patterns. The game's log format has probably changed; tracking is incomplete.",
                    stats.UnrecognisedMoves);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-pass; the next launch picks up from the watermark.
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
            _placesStale = true;
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
