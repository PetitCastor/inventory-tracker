using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using InventoryTracker.App;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Naming;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace InventoryTracker.Tests;

/// <summary>Tests the shared request policy through an injected, manually advanced clock.</summary>
public sealed class WikiRefreshTests
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Default_service_construction_uses_the_coordinator_shared_by_app_callers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"itrk-wiki-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new TrackerDb(Path.Combine(directory, "tracker.db"));
        db.Initialize();
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(Ok())));
        var options = new TrackerOptions { LogDir = directory };

        var names = new WikiNameService(db, http);
        var vehicles = new WikiVehicleCatalog(db, http);
        var places = new PlaceNameService(db, options, http);

        Assert.Same(WikiRefreshCoordinator.Shared, names.RefreshCoordinator);
        Assert.Same(names.RefreshCoordinator, vehicles.RefreshCoordinator);
        Assert.Same(names.RefreshCoordinator, places.RefreshCoordinator);

        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Successful_refresh_pipeline_publishes_names_before_rebuilding_the_snapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"itrk-wiki-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new TrackerDb(Path.Combine(directory, "tracker.db"));
        db.Initialize();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var coordinator = new WikiRefreshCoordinator(clock);
        using var http = new HttpClient(new RecordingHandler(clock, (request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/api/items" => Json("{\"data\":[{\"class_name\":\"fresh_item\",\"name\":\"Fresh item\"}],\"meta\":{\"last_page\":1}}"),
                "/api/vehicles" => Json("{\"data\":[],\"meta\":{\"last_page\":1}}"),
                "/api/locations" => Json("{\"data\":[],\"meta\":{\"last_page\":1}}"),
                _ => throw new InvalidOperationException($"Unexpected wiki endpoint {request.RequestUri}")
            })));
        var options = new TrackerOptions { LogDir = directory };
        var names = new WikiNameService(db, http, coordinator);
        var places = new PlaceNameService(db, options, http, coordinator);
        var state = new TrackerState(db, names, options);

        try
        {
            var callerRefresh = WikiRefreshCallerPipeline.RunAsync(
                () => names.RefreshAsync(),
                () => places.RefreshAsync(),
                () => { },
                state.Rebuild);
            await CompleteWhileAdvancingClockAsync(callerRefresh, clock);
            Assert.Empty(await callerRefresh);

            Assert.Equal("Fresh item", names.Resolve("fresh_item").Display);
            Assert.True(state.Current.BuiltAt > DateTimeOffset.MinValue);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Shared_caller_pipeline_keeps_independent_failures_visible_and_rebuilds()
    {
        var placesSucceeded = false;
        var rebuilt = false;
        var expected = new InvalidOperationException("item endpoint offline");

        var failures = await WikiRefreshCallerPipeline.RunAsync(
            () => Task.FromException(expected),
            () =>
            {
                placesSucceeded = true;
                return Task.CompletedTask;
            },
            () => { },
            () => rebuilt = true);

        Assert.True(placesSucceeded);
        Assert.True(rebuilt);
        var failure = Assert.Single(failures);
        Assert.Equal("item names", failure.Source);
        Assert.Same(expected, failure.Exception);
    }

    [Fact]
    public async Task Concurrent_catalogue_kinds_share_one_start_cadence()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var coordinator = new WikiRefreshCoordinator(clock);
        var handler = new RecordingHandler(clock, (_, _) => Task.FromResult(Ok()));
        using var http = new HttpClient(handler);

        var item = coordinator.SendAsync(http, Get("https://wiki.test/items"));
        var vehicle = coordinator.SendAsync(http, Get("https://wiki.test/vehicles"));
        var place = coordinator.SendAsync(http, Get("https://wiki.test/locations"));

        var all = Task.WhenAll(item, vehicle, place);
        while (!all.IsCompleted)
        {
            var delay = await clock.ReadDelayOrCompletionAsync(all, TestTimeout);
            if (delay is null) break;
            clock.AdvanceBy(delay.Value);
        }
        await all.WaitAsync(TestTimeout);

        Assert.Equal(3, handler.Starts.Count);
        var starts = handler.Starts.Select(x => x.At).Order().ToArray();
        Assert.True(starts[1] - starts[0] >= Cadence, $"start gap was {starts[1] - starts[0]}");
        Assert.True(starts[2] - starts[1] >= Cadence, $"start gap was {starts[2] - starts[1]}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_after_delta_and_http_date_are_honored(bool useDate)
    {
        var now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
        var clock = new ManualClock(now);
        var retryAfter = TimeSpan.FromSeconds(7);
        var attempts = 0;
        var handler = new RecordingHandler(clock, (_, _) => Task.FromResult(
            Interlocked.Increment(ref attempts) == 1
                ? TooManyRequests(useDate ? null : retryAfter, useDate ? now + retryAfter : null)
                : Ok()));
        using var http = new HttpClient(handler);
        var coordinator = new WikiRefreshCoordinator(clock);

        var send = coordinator.SendAsync(http, Get("https://wiki.test/items"));
        var observed = await clock.ReadDelayOrTimeoutAsync(TestTimeout);
        Assert.Equal(retryAfter, observed);
        clock.AdvanceBy(retryAfter);
        using var response = await send.WaitAsync(TestTimeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var starts = handler.Starts.ToArray();
        Assert.Equal(2, starts.Length);
        Assert.True(starts[1].At - starts[0].At >= retryAfter);
    }

    [Fact]
    public async Task Retry_after_over_30_seconds_fails_without_an_early_retry()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var handler = new RecordingHandler(clock, (_, _) => Task.FromResult(
            TooManyRequests(TimeSpan.FromSeconds(31), null)));
        using var http = new HttpClient(handler);
        var coordinator = new WikiRefreshCoordinator(clock);

        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var response = await coordinator.SendAsync(http, Get("https://wiki.test/items"));
        });

        Assert.Contains("Retry-After", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Starts);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task Retry_count_is_bounded_and_final_429_remains_visible()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var handler = new RecordingHandler(clock, (_, _) => Task.FromResult(TooManyRequests(null, null)));
        using var http = new HttpClient(handler);
        var coordinator = new WikiRefreshCoordinator(clock);
        using var cts = new CancellationTokenSource();

        var send = coordinator.SendAsync(http, Get("https://wiki.test/items"), cts.Token);
        var firstDelay = await clock.ReadDelayOrTimeoutAsync(TestTimeout);
        Assert.NotNull(firstDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), firstDelay.Value);
        clock.AdvanceBy(firstDelay.Value);
        var secondDelay = await clock.ReadDelayOrTimeoutAsync(TestTimeout);
        Assert.NotNull(secondDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), secondDelay.Value);
        clock.AdvanceBy(secondDelay.Value);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => send.WaitAsync(TestTimeout));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(3, handler.Starts.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, clock.Delays);
    }

    [Fact]
    public async Task Cancellation_during_cooldown_prevents_the_next_request()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var handler = new RecordingHandler(clock, (_, _) => Task.FromResult(Ok()));
        using var http = new HttpClient(handler);
        var coordinator = new WikiRefreshCoordinator(clock);
        using var cts = new CancellationTokenSource();

        var first = coordinator.SendAsync(http, Get("https://wiki.test/items/one"), cts.Token);
        using var firstResponse = await first.WaitAsync(TestTimeout);
        var second = coordinator.SendAsync(http, Get("https://wiki.test/items/two"), cts.Token);
        var delay = await clock.ReadDelayOrTimeoutAsync(TestTimeout);
        Assert.Equal(Cadence, delay);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Single(handler.Starts);
    }

    [Fact]
    public async Task Identical_concurrent_gets_are_deduplicated()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var handler = new RecordingHandler(clock, async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Ok();
        });
        using var http = new HttpClient(handler);
        var coordinator = new WikiRefreshCoordinator(clock);

        var first = coordinator.SendAsync(http, Get("https://wiki.test/items?page=1"));
        await started.Task.WaitAsync(TestTimeout);
        var second = coordinator.SendAsync(http, Get("https://wiki.test/items?page=1"));
        release.TrySetResult();
        using var firstResponse = await first.WaitAsync(TestTimeout);
        using var secondResponse = await second.WaitAsync(TestTimeout);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Single(handler.Starts);
    }

    [Fact]
    public async Task Concurrent_identical_name_refreshes_share_one_catalogue_generation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"itrk-wiki-dedup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new TrackerDb(Path.Combine(directory, "tracker.db"));
        db.Initialize();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var coordinator = new WikiRefreshCoordinator(clock);
        var itemStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstItem = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(clock, async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/items")
            {
                itemStarted.TrySetResult();
                await releaseFirstItem.Task.WaitAsync(ct);
                return Json("{\"data\":[{\"class_name\":\"fresh_item\",\"name\":\"Fresh item\"}],\"meta\":{\"last_page\":1}}");
            }

            return Json("{\"data\":[],\"meta\":{\"last_page\":1}}");
        });
        using var http = new HttpClient(handler);
        var names = new WikiNameService(db, http, coordinator);

        try
        {
            var first = names.RefreshAsync();
            await itemStarted.Task.WaitAsync(TestTimeout);
            var second = names.RefreshAsync();
            releaseFirstItem.TrySetResult();
            await CompleteWhileAdvancingClockAsync(Task.WhenAll(first, second), clock);

            var itemRequests = handler.Starts.Count(x => new Uri(x.Uri).AbsolutePath == "/api/items");
            var vehicleRequests = handler.Starts.Count(x => new Uri(x.Uri).AbsolutePath == "/api/vehicles");
            Assert.Equal(1, itemRequests);
            Assert.Equal(1, vehicleRequests);
            Assert.Equal("Fresh item", names.Resolve("fresh_item").Display);
        }
        finally
        {
            releaseFirstItem.TrySetResult();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Log_watch_stop_waits_for_its_startup_refreshes_to_finish()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"itrk-wiki-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new TrackerDb(Path.Combine(directory, "tracker.db"));
        db.Initialize();
        var options = new TrackerOptions { LogDir = directory };
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var coordinator = new WikiRefreshCoordinator(clock);
        var requestCount = 0;
        var activeRequests = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAfterCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestsFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledRequests = 0;
        var bothCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref activeRequests);
            firstStarted.TrySetResult();
            if (Interlocked.Increment(ref requestCount) >= 2) bothStarted.TrySetResult();
            try
            {
                await holdRequests.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[],\"meta\":{\"last_page\":1}}"),
                };
            }
            catch (OperationCanceledException)
            {
                if (Interlocked.Increment(ref canceledRequests) == 2) bothCanceled.TrySetResult();
                await releaseAfterCancellation.Task;
                throw;
            }
            finally
            {
                if (Interlocked.Decrement(ref activeRequests) == 0) requestsFinished.TrySetResult();
            }
        }));
        var names = new WikiNameService(db, http, coordinator);
        var places = new PlaceNameService(db, options, http, coordinator);
        var state = new TrackerState(db, names, options);
        var service = new LogWatchService(options, db, state, names, places, NullLogger<LogWatchService>.Instance);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await firstStarted.Task.WaitAsync(TestTimeout);
            while (!bothStarted.Task.IsCompleted)
            {
                var delay = await clock.ReadDelayOrCompletionAsync(bothStarted.Task, TestTimeout);
                if (delay is null) break;
                clock.AdvanceBy(delay.Value);
            }
            await bothStarted.Task.WaitAsync(TestTimeout);
            var stop = service.StopAsync(CancellationToken.None);

            await bothCanceled.Task.WaitAsync(TestTimeout);
            Assert.Equal(2, canceledRequests);
            Assert.False(stop.IsCompleted, "host stop completed before its canceled refresh tasks finished");

            releaseAfterCancellation.TrySetResult();
            await stop.WaitAsync(TestTimeout);
            await requestsFinished.Task.WaitAsync(TestTimeout);
            Assert.Equal(2, requestCount);
            Assert.Equal(0, activeRequests);
        }
        finally
        {
            releaseAfterCancellation.TrySetResult();
            await requestsFinished.Task.WaitAsync(TestTimeout);
            service.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Wiki_network_wait_does_not_hold_a_sqlite_transaction()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"itrk-wiki-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new TrackerDb(Path.Combine(directory, "tracker.db"));
        db.Initialize();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var networkWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        Task<int>? refresh = null;
        using var http = new HttpClient(new DelegateHandler(async (_, ct) =>
        {
            requestStarted.TrySetResult();
            await networkWait.Task.WaitAsync(ct);
            throw new InvalidOperationException("Unreachable");
        }));

        try
        {
            refresh = new WikiNameService(db, http).RefreshAsync(ct: cts.Token);
            await requestStarted.Task.WaitAsync(TestTimeout);
            var write = Task.Run(() =>
            {
                using var cn = db.Open();
                using var tx = cn.BeginTransaction();
                using var cmd = cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO meta(key, value) VALUES('l07_writer_probe', 'committed')";
                cmd.ExecuteNonQuery();
                tx.Commit();
            });
            await write.WaitAsync(TestTimeout);

            using (var cn = db.Open())
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM meta WHERE key = 'l07_writer_probe'";
                Assert.Equal("committed", cmd.ExecuteScalar());
            }
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh!);
        }
        finally
        {
            cts.Cancel();
            if (refresh is not null)
            {
                await ObserveCompletionAsync(refresh);
            }
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Tray_refresh_owner_cancels_and_awaits_work_before_shutdown_completes()
    {
        var owner = new WikiRefreshTaskOwner();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? refresh = null;

        try
        {
            refresh = TrayHost.RunTrayRefreshAsync(owner, async ct =>
            {
                started.TrySetResult();
                try
                {
                    await releaseWork.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    await releaseShutdown.Task;
                    throw;
                }
            });
            await started.Task.WaitAsync(TestTimeout);
            var stop = owner.StopAsync();

            await cancelled.Task.WaitAsync(TestTimeout);
            Assert.False(stop.IsCompleted, "tray shutdown completed before its canceled refresh finished");
            releaseShutdown.TrySetResult();
            await stop.WaitAsync(TestTimeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        }
        finally
        {
            releaseWork.TrySetResult();
            releaseShutdown.TrySetResult();
            if (refresh is not null)
            {
                await ObserveCompletionAsync(refresh);
            }
        }
    }

    [Fact]
    public async Task Tray_shutdown_joins_refresh_without_pumping_its_captured_ui_context()
    {
        var owner = new WikiRefreshTaskOwner();
        var uiContext = new PausedSynchronizationContext();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackContext = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopFailure = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? refresh = null;

        var uiThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            try
            {
                refresh = TrayHost.RunTrayRefreshAsync(owner, async ct =>
                {
                    callbackContext.TrySetResult(SynchronizationContext.Current);
                    callbackStarted.TrySetResult();
                    try
                    {
                        await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task
                            .WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.TrySetResult();
                        await releaseCancellationCleanup.Task.ConfigureAwait(false);
                        throw;
                    }
                });

                // The real shutdown join runs after the WinForms message loop stops. Wait
                // until the owned callback has started, then stop pumping this UI context.
                callbackStarted.Task.GetAwaiter().GetResult();

                stopStarted.TrySetResult();
                StopTrayOwnerSynchronously(owner);
                stopFailure.TrySetResult(null);
            }
            catch (Exception ex)
            {
                stopFailure.TrySetResult(ex);
            }
            finally
            {
                stopFinished.TrySetResult();
                SynchronizationContext.SetSynchronizationContext(null);
            }
        })
        {
            IsBackground = true,
            Name = "Wiki refresh UI context test",
        };

        uiThread.Start();
        var callbackRanWithoutUiContext = false;
        var joinedWithoutUiPump = false;
        var cleanupWasAwaited = false;
        try
        {
            var callbackExecutionContext = await callbackContext.Task.WaitAsync(TestTimeout);
            callbackRanWithoutUiContext = callbackExecutionContext is null;
            await stopStarted.Task.WaitAsync(TestTimeout);
            await cancellationObserved.Task.WaitAsync(TestTimeout);
            cleanupWasAwaited = !stopFinished.Task.IsCompleted;
            releaseCancellationCleanup.TrySetResult();
            var firstProgress = await Task.WhenAny(stopFinished.Task, uiContext.WorkPosted).WaitAsync(TestTimeout);
            joinedWithoutUiPump = firstProgress == stopFinished.Task && stopFinished.Task.IsCompleted;
            if (joinedWithoutUiPump) await stopFinished.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            // The old implementation captures the UI context in its owned task. Pump only
            // after recording whether shutdown needed that context, to let the test thread exit.
            releaseCancellationCleanup.TrySetResult();
            while (!stopFinished.Task.IsCompleted)
            {
                if (!uiContext.TryRunOne()) uiContext.WaitForWork(TestTimeout);
            }

            Assert.True(uiThread.Join(TestTimeout), "the simulated UI thread did not exit");
        }

        Assert.True(callbackRanWithoutUiContext, "the owned callback inherited the UI SynchronizationContext");
        Assert.True(cleanupWasAwaited, "the owner completed before canceled callback cleanup finished");
        Assert.True(joinedWithoutUiPump, "tray shutdown needed the stopped UI context to finish joining refresh work");
        Assert.Null(await stopFailure.Task);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh!);
    }

    [Fact]
    public async Task Settings_owned_refresh_cancels_and_awaits_pipeline_before_disposal_completes()
    {
        var owner = new WikiRefreshTaskOwner();
        var placesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var placeCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken itemToken = default;
        CancellationToken placeToken = default;
        Task? refresh = null;

        try
        {
            refresh = owner.RunAsync(async ct =>
            {
                await WikiRefreshCallerPipeline.RunAsync(
                    () =>
                    {
                        itemToken = ct;
                        return Task.CompletedTask;
                    },
                    async () =>
                    {
                        placeToken = ct;
                        placesStarted.TrySetResult();
                        try
                        {
                            await releasePlace.Task.WaitAsync(ct);
                        }
                        catch (OperationCanceledException)
                        {
                            placeCanceled.TrySetResult();
                            await releaseCancellationCleanup.Task;
                            throw;
                        }
                    },
                    () => { },
                    () => throw new InvalidOperationException("Canceled Settings refresh must not rebuild or report success."),
                    ct);
            });

            await placesStarted.Task.WaitAsync(TestTimeout);
            Assert.Equal(itemToken, placeToken);
            var disposal = owner.StopAsync();

            await placeCanceled.Task.WaitAsync(TestTimeout);
            Assert.True(itemToken.IsCancellationRequested);
            Assert.True(placeToken.IsCancellationRequested);
            Assert.False(disposal.IsCompleted, "Settings disposal completed before its canceled refresh finished.");

            releaseCancellationCleanup.TrySetResult();
            await disposal.WaitAsync(TestTimeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        }
        finally
        {
            releasePlace.TrySetResult();
            releaseCancellationCleanup.TrySetResult();
            if (refresh is not null) await ObserveCompletionAsync(refresh);
        }
    }

    private static HttpRequestMessage Get(string uri) => new(HttpMethod.Get, uri);

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{}"),
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static async Task CompleteWhileAdvancingClockAsync(Task operation, ManualClock clock)
    {
        while (!operation.IsCompleted)
        {
            var delay = await clock.ReadDelayOrCompletionAsync(operation, TestTimeout);
            if (delay is null) break;
            clock.AdvanceBy(delay.Value);
        }

        await operation.WaitAsync(TestTimeout);
    }

    private static Task ObserveCompletionAsync(Task operation) => operation.ContinueWith(
        static completed =>
        {
            if (completed.IsFaulted) _ = completed.Exception;
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static void StopTrayOwnerSynchronously(WikiRefreshTaskOwner owner) =>
        owner.StopAsync().GetAwaiter().GetResult();

    private static HttpResponseMessage TooManyRequests(TimeSpan? delta, DateTimeOffset? date)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (delta is { } d) response.Headers.RetryAfter = new RetryConditionHeaderValue(d);
        if (date is { } at) response.Headers.RetryAfter = new RetryConditionHeaderValue(at);
        return response;
    }

    private sealed class RecordingHandler(
        IWikiRefreshClock clock,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Uri, DateTimeOffset At)> Starts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Starts.Enqueue((request.RequestUri!.ToString(), clock.UtcNow));
            return send(request, ct);
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }

    private sealed class PausedSynchronizationContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private readonly ManualResetEventSlim _workAvailable = new();
        private readonly TaskCompletionSource _workPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WorkPosted => _workPosted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                _pending.Enqueue((callback, state));
                _workAvailable.Set();
            }
            _workPosted.TrySetResult();
        }

        public bool TryRunOne()
        {
            (SendOrPostCallback Callback, object? State) work;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out work))
                {
                    _workAvailable.Reset();
                    return false;
                }

                if (_pending.Count == 0) _workAvailable.Reset();
            }

            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                work.Callback(work.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }

            return true;
        }

        public void WaitForWork(TimeSpan timeout)
        {
            if (!_workAvailable.Wait(timeout))
                throw new TimeoutException("No continuation reached the paused UI context.");
        }
    }

    private sealed class ManualClock(DateTimeOffset initialTime) : IWikiRefreshClock
    {
        private sealed record Waiter(DateTimeOffset DueAt, TaskCompletionSource Completion);

        private readonly object _gate = new();
        private readonly List<Waiter> _waiters = [];
        private readonly ConcurrentQueue<TimeSpan> _delays = new();
        private readonly SemaphoreSlim _newDelay = new(0);
        private DateTimeOffset _now = initialTime;

        public DateTimeOffset UtcNow
        {
            get { lock (_gate) return _now; }
        }

        public IReadOnlyList<TimeSpan> Delays => _delays.ToArray();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _waiters.Add(new Waiter(_now + delay, completion));
            _delays.Enqueue(delay);
            _newDelay.Release();
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public async Task<TimeSpan?> ReadDelayOrTimeoutAsync(TimeSpan timeout)
        {
            try
            {
                await _newDelay.WaitAsync(timeout);
                return _delays.LastOrDefault();
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public async Task<TimeSpan?> ReadDelayOrCompletionAsync(Task operation, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource();
            var nextDelay = _newDelay.WaitAsync(cancellation.Token);
            var winner = await Task.WhenAny(operation, nextDelay).WaitAsync(timeout);
            if (winner == operation)
            {
                cancellation.Cancel();
                return null;
            }

            await nextDelay;
            return _delays.LastOrDefault();
        }

        public void AdvanceBy(TimeSpan amount)
        {
            List<TaskCompletionSource> ready;
            lock (_gate)
            {
                _now += amount;
                ready = _waiters.Where(x => x.DueAt <= _now).Select(x => x.Completion).ToList();
                _waiters.RemoveAll(x => x.DueAt <= _now);
            }

            foreach (var completion in ready) completion.TrySetResult();
        }
    }
}
