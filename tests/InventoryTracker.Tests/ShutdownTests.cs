using InventoryTracker.App;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Naming;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests;

public sealed class ShutdownTests
{
    private const string Store =
        "<2026-08-27T00:40:00.000Z> [Notice] <InventoryManagementRequest> Queued Request[1] " +
        "Type[Store] for 'Pilot' [2001] Source Inventory[INVALID] " +
        "Target Inventory[204821708183:Location:4005457614]. " +
        "Source[NONE] amount[1] rank[0]. Target[NONE] amount[0] rank[0]. " +
        "Item[grin_multitool_01_red01_481104483777] action[none]";

    [Fact]
    public void Application_stopping_posts_exit_to_the_tray_message_loop()
    {
        using var lifetime = new TestApplicationLifetime();
        var context = new QueuedSynchronizationContext();
        var exitThreadCalls = 0;
        using var registration = TrayHost.RegisterMessageLoopStop(
            lifetime, context, () => exitThreadCalls++);

        lifetime.Stop();

        Assert.Equal(0, exitThreadCalls);
        Assert.True(context.HasPostedCallback);
        context.RunPostedCallback();
        Assert.Equal(1, exitThreadCalls);
    }

    [Fact]
    public async Task Wiki_refresh_owner_cancels_and_joins_work_before_stop_returns()
    {
        var owner = new WikiRefreshTaskOwner();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = owner.RunAsync(async ct =>
        {
            started.SetResult(ct);
            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => wait.TrySetCanceled(ct));
            try
            {
                await wait.Task;
            }
            finally
            {
                finished.SetResult();
            }
        });
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await owner.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(token.IsCancellationRequested);
        Assert.True(finished.Task.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public async Task Log_watch_stop_cancels_and_joins_startup_wiki_refreshes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"itrk-shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var db = new TrackerDb(Path.Combine(dir, "tracker.db"));
        db.Initialize();
        var options = new TrackerOptions { LogDir = dir, DatabasePath = db.Path };
        using var handler = new BlockingRequestHandler();
        using var namesHttp = new HttpClient(handler, disposeHandler: false);
        using var placesHttp = new HttpClient(handler, disposeHandler: false);
        var names = new WikiNameService(db, namesHttp);
        var state = new TrackerState(db, names, options);
        var watcher = new LogWatchService(
            options, db, state, names, new PlaceNameService(db, options, placesHttp),
            NullLogger<LogWatchService>.Instance);

        try
        {
            await watcher.StartAsync(CancellationToken.None);
            var requestToken = await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await watcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(requestToken.IsCancellationRequested);
            Assert.True(handler.Finished.Task.IsCompleted);
        }
        finally
        {
            watcher.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Reset_canceled_after_lock_acquisition_does_not_wipe_stored_state()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"itrk-reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var db = new TrackerDb(Path.Combine(dir, "tracker.db"));
        db.Initialize();
        var options = new TrackerOptions { LogDir = dir, DatabasePath = db.Path };
        using var namesHttp = new HttpClient();
        using var placesHttp = new HttpClient();
        var names = new WikiNameService(db, namesHttp);
        var state = new TrackerState(db, names, options);
        var watcher = new LogWatchService(
            options, db, state, names, new PlaceNameService(db, options, placesHttp),
            NullLogger<LogWatchService>.Instance);

        try
        {
            File.WriteAllText(Path.Combine(dir, "Game.log"), Store + "\n");
            await watcher.ResetAndRebuildAsync();
            Assert.NotEmpty(state.Current.Holdings);

            var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var reset = watcher.ResetAndRebuildAsync(cancellation.Token, async () =>
            {
                acquired.SetResult();
                await continueReset.Task;
            });

            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            continueReset.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
            Assert.NotEmpty(state.Current.Holdings);
            Assert.Equal(1, IngestHealth.Load(db, dir, null).Moves);
        }
        finally
        {
            watcher.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Reset_canceled_after_wipe_finishes_rebuild_before_reporting_cancellation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"itrk-reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var db = new TrackerDb(Path.Combine(dir, "tracker.db"));
        db.Initialize();
        var options = new TrackerOptions { LogDir = dir, DatabasePath = db.Path };
        using var namesHttp = new HttpClient();
        using var placesHttp = new HttpClient();
        var names = new WikiNameService(db, namesHttp);
        var state = new TrackerState(db, names, options);
        var watcher = new LogWatchService(
            options, db, state, names, new PlaceNameService(db, options, placesHttp),
            NullLogger<LogWatchService>.Instance);

        try
        {
            File.WriteAllText(Path.Combine(dir, "Game.log"), Store + "\n");
            await watcher.ResetAndRebuildAsync();
            Assert.NotEmpty(state.Current.Holdings);
            options.InceptionDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

            var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueRebuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var reset = watcher.ResetAndRebuildAsync(cancellation.Token, afterLockAcquired: null, async () =>
            {
                resetStarted.SetResult();
                await continueRebuild.Task;
            });

            await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            continueRebuild.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
            Assert.Empty(state.Current.Holdings);
            Assert.Equal(0, IngestHealth.Load(db, dir, options.InceptionDate).Moves);
        }
        finally
        {
            watcher.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private SendOrPostCallback? _callback;
        private object? _state;

        public bool HasPostedCallback => _callback is not null;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
        }

        public void RunPostedCallback()
        {
            var callback = _callback ?? throw new InvalidOperationException("No callback was posted.");
            _callback = null;
            callback(_state);
        }
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void Stop() => _stopping.Cancel();

        public void StopApplication() => Stop();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private sealed class BlockingRequestHandler : HttpMessageHandler
    {
        public TaskCompletionSource<CancellationToken> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(cancellationToken);
            var wait = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => wait.TrySetCanceled(cancellationToken));
            try
            {
                return await wait.Task;
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }
}
