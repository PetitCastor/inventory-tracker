using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace InventoryTracker.Naming;

/// <summary>Clock seam used to make the wiki request policy deterministic in tests.</summary>
public interface IWikiRefreshClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Coordinates wiki request starts and retries within this process.</summary>
public sealed class WikiRefreshCoordinator
{
    private sealed class SystemWikiRefreshClock : IWikiRefreshClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    private sealed record RequestKey(HttpClient Client, string Method, string Uri, string Headers);

    private sealed class InflightGet
    {
        public TaskCompletionSource<ResponseSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public object Gate { get; } = new();
        public int Waiters { get; set; }
        public bool Started { get; set; }
    }

    private sealed record ResponseSnapshot(
        HttpStatusCode StatusCode,
        string? ReasonPhrase,
        Version Version,
        KeyValuePair<string, IEnumerable<string>>[] Headers,
        KeyValuePair<string, IEnumerable<string>>[] ContentHeaders,
        byte[] Content)
    {
        public HttpResponseMessage CreateResponse()
        {
            var response = new HttpResponseMessage(StatusCode)
            {
                ReasonPhrase = ReasonPhrase,
                Version = Version,
                Content = new ByteArrayContent(Content),
            };
            foreach (var (name, values) in Headers) response.Headers.TryAddWithoutValidation(name, values);
            foreach (var (name, values) in ContentHeaders) response.Content.Headers.TryAddWithoutValidation(name, values);
            return response;
        }

        public static async Task<ResponseSnapshot> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var content = response.Content is null
                ? []
                : await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseSnapshot(
                response.StatusCode,
                response.ReasonPhrase,
                response.Version,
                response.Headers.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value.ToArray())).ToArray(),
                response.Content?.Headers.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value.ToArray())).ToArray() ?? [],
                content);
        }
    }

    private static readonly TimeSpan MinimumStartGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] FallbackRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    public static WikiRefreshCoordinator Shared { get; } = new(new SystemWikiRefreshClock());

    private readonly IWikiRefreshClock _clock;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<RequestKey, InflightGet> _inflightGets = new();
    private DateTimeOffset? _lastStartedAt;

    public WikiRefreshCoordinator(IWikiRefreshClock? clock = null)
    {
        _clock = clock ?? new SystemWikiRefreshClock();
    }

    /// <summary>Current local time for Retry-After date calculations.</summary>
    public DateTimeOffset UtcNow => _clock.UtcNow;

    /// <summary>Waits through the injected clock.</summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        _clock.DelayAsync(delay, cancellationToken);

    /// <summary>Sends a wiki request, retrying only 429 responses and cloning each attempt.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var content = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentHeaders = request.Content?.Headers
            .Select(x => new KeyValuePair<string, string[]>(x.Key, x.Value.ToArray()))
            .ToArray() ?? [];
        var headers = request.Headers
            .Select(x => new KeyValuePair<string, string[]>(x.Key, x.Value.ToArray()))
            .ToArray();
        var method = request.Method;
        var uri = request.RequestUri;
        var version = request.Version;
        var versionPolicy = request.VersionPolicy;

        HttpRequestMessage CreateRequest()
        {
            var clone = new HttpRequestMessage(method, uri)
            {
                Version = version,
                VersionPolicy = versionPolicy,
            };
            foreach (var (name, values) in headers) clone.Headers.TryAddWithoutValidation(name, values);
            if (content is not null)
            {
                clone.Content = new ByteArrayContent(content);
                foreach (var (name, values) in contentHeaders)
                    clone.Content.Headers.TryAddWithoutValidation(name, values);
            }
            return clone;
        }

        try
        {
            return await SendAsync(client, CreateRequest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>Sends a request from a factory so every retry gets a fresh message.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var first = requestFactory();
        if (first.Method != HttpMethod.Get || first.RequestUri is null || first.Content is not null)
        {
            return await SendWithRetriesAsync(client, requestFactory, cancellationToken).ConfigureAwait(false);
        }

        var headerKey = string.Join("\n", first.Headers
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}:{string.Join(",", x.Value)}"));
        var key = new RequestKey(client, first.Method.Method, first.RequestUri.AbsoluteUri, headerKey);
        var entry = _inflightGets.GetOrAdd(key, static _ => new InflightGet());
        var start = false;
        lock (entry.Gate)
        {
            entry.Waiters++;
            if (!entry.Started)
            {
                entry.Started = true;
                start = true;
            }
        }

        if (start) _ = CompleteGetAsync(key, entry, client, requestFactory);
        try
        {
            return (await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false)).CreateResponse();
        }
        finally
        {
            var waitForCleanup = false;
            lock (entry.Gate)
            {
                entry.Waiters--;
                if (entry.Waiters == 0 && cancellationToken.IsCancellationRequested && !entry.Cleanup.Task.IsCompleted)
                {
                    if (!entry.Completion.Task.IsCompleted) entry.Cancellation.Cancel();
                    waitForCleanup = true;
                }
            }
            if (waitForCleanup)
            {
                await entry.Cleanup.Task.ConfigureAwait(false);
                _ = entry.Completion.Task.Exception;
            }
        }
    }

    private async Task CompleteGetAsync(
        RequestKey key,
        InflightGet entry,
        HttpClient client,
        Func<HttpRequestMessage> requestFactory)
    {
        ResponseSnapshot? snapshot = null;
        Exception? failure = null;
        var wasCanceled = false;
        var cancellationToken = entry.Cancellation.Token;
        try
        {
            using var response = await SendWithRetriesAsync(client, requestFactory, entry.Cancellation.Token).ConfigureAwait(false);
            snapshot = await ResponseSnapshot.ReadAsync(response, entry.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            wasCanceled = ex is OperationCanceledException && entry.Cancellation.IsCancellationRequested;
        }
        finally
        {
            _inflightGets.TryRemove(new KeyValuePair<RequestKey, InflightGet>(key, entry));
            lock (entry.Gate)
            {
                entry.Cancellation.Dispose();
                if (wasCanceled) entry.Completion.TrySetCanceled(cancellationToken);
                else if (failure is not null) entry.Completion.TrySetException(failure);
                else entry.Completion.TrySetResult(snapshot!);
                entry.Cleanup.TrySetResult();
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = requestFactory();
            await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task<HttpResponseMessage> sending;
            try
            {
                if (_lastStartedAt is { } last)
                {
                    var wait = last + MinimumStartGap - _clock.UtcNow;
                    if (wait > TimeSpan.Zero) await _clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                _lastStartedAt = _clock.UtcNow;
                sending = client.SendAsync(request, cancellationToken);
            }
            finally
            {
                _startGate.Release();
            }

            var response = await sending.ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;

            if (attempt >= FallbackRetryDelays.Length)
            {
                response.Dispose();
                throw new HttpRequestException("The wiki request remained rate limited after three attempts.", null, HttpStatusCode.TooManyRequests);
            }

            TimeSpan retryDelay;
            try
            {
                retryDelay = GetRetryDelay(response, attempt);
            }
            catch (Exception)
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
            await _clock.DelayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return FallbackRetryDelays[attempt];

        var delay = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - _clock.UtcNow : TimeSpan.Zero);
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (delay > MaximumRetryAfter)
        {
            throw new HttpRequestException(
                $"The wiki Retry-After delay of {delay} exceeds the local maximum of {MaximumRetryAfter}.",
                null,
                HttpStatusCode.TooManyRequests);
        }

        return delay;
    }
}

/// <summary>One shared, testable ordering for the tray and Settings refresh actions.</summary>
public static class WikiRefreshCallerPipeline
{
    public sealed record Failure(string Source, Exception Exception);

    public static async Task<IReadOnlyList<Failure>> RunAsync(
        Func<Task> refreshItems,
        Func<Task> refreshPlaces,
        Action placesRefreshed,
        Action rebuildSnapshot,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<Failure>();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await refreshItems();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add(new Failure("item names", ex));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await refreshPlaces();
            placesRefreshed();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add(new Failure("the place list", ex));
        }

        cancellationToken.ThrowIfCancellationRequested();
        rebuildSnapshot();
        return failures;
    }
}

/// <summary>Tracks and cancels wiki refresh work started by the tray menu.</summary>
public sealed class WikiRefreshTaskOwner
{
    private readonly object _gate = new();
    private readonly Dictionary<Task, CancellationTokenSource> _tasks = [];
    private bool _stopping;

    public bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    public Task RunAsync(Func<CancellationToken, Task> refresh)
    {
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping)
            {
                cancellation.Dispose();
                throw new InvalidOperationException("The tray refresh owner is stopping.");
            }
            _tasks.Add(completion.Task, cancellation);
        }

        _ = RunOwnedAsync(refresh, completion, cancellation);
        return completion.Task;
    }

    private async Task RunOwnedAsync(
        Func<CancellationToken, Task> refresh,
        TaskCompletionSource completion,
        CancellationTokenSource cancellation)
    {
        try
        {
            await refresh(cancellation.Token).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate) _tasks.Remove(completion.Task);
            cancellation.Dispose();
        }
    }

    public async Task StopAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            _stopping = true;
            tasks = _tasks.Keys.ToArray();
            foreach (var cancellation in _tasks.Values) cancellation.Cancel();
        }
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (tasks.Any(task => task.IsCanceled))
        {
            System.Diagnostics.Trace.WriteLine($"Tray refresh work canceled during shutdown: {ex.Message}");
        }
        catch (Exception ex) when (tasks.Any(task => task.IsFaulted))
        {
            // The tray event handler reports refresh failures; shutdown still has to finish joining work.
            System.Diagnostics.Trace.WriteLine($"Tray refresh failed while shutting down: {ex}");
        }
    }
}
