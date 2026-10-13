using System.Net;
using InventoryTracker.App;

namespace InventoryTracker.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task DownloadAsync_BodyThatStallsAfterHeaders_ExpiresAndRemovesPartialFile()
    {
        var clock = new ManualTimeProvider();
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var service = new UpdateService(http, TimeSpan.FromSeconds(5), clock);
        var info = NewInfo("test-expiry");
        var download = service.DownloadAsync(info);

        try
        {
            var bodyToken = await handler.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.True(bodyToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(DownloadPaths(info));
        }
        finally
        {
            handler.Body.Unblock();
            if (!download.IsCompleted) await download.WaitAsync(TimeSpan.FromSeconds(5));
            DeleteDownloadPaths(info);
        }
    }

    [Fact]
    public async Task DownloadAsync_ExternalCancellationCancelsBlockedBodyAndRemovesPartialFile()
    {
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var service = new UpdateService(http, TimeSpan.FromMinutes(5), TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var info = NewInfo("test-cancel");
        var download = service.DownloadAsync(info, ct: cancel.Token);
        var bodyToken = await handler.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.True(bodyToken.IsCancellationRequested);
        Assert.Empty(DownloadPaths(info));
        DeleteDownloadPaths(info);
    }

    [Fact]
    public async Task StartupUpdate_ExpiredBodyUsesRecoveryPathAndDoesNotInstall()
    {
        var clock = new ManualTimeProvider();
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var service = new UpdateService(http, TimeSpan.FromSeconds(5), clock);
        var info = NewInfo("test-startup-expiry");
        var installAttempted = false;
        var startup = TrayHost.TryDownloadAndApplyStartupUpdateAsync(service, info, _ => installAttempted = true);

        try
        {
            await handler.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(5));

            Assert.False(await startup.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(installAttempted);
            Assert.Empty(DownloadPaths(info));
        }
        finally
        {
            handler.Body.Unblock();
            if (!startup.IsCompleted) await startup.WaitAsync(TimeSpan.FromSeconds(5));
            DeleteDownloadPaths(info);
        }
    }

    [Fact]
    public async Task DownloadAsync_CompleteResponse_ReturnsReadableFile()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        using var http = new HttpClient(new FixedHandler(bytes));
        var service = new UpdateService(http, TimeSpan.FromMinutes(5), TimeProvider.System);
        var info = NewInfo("test-complete", bytes.Length);
        var path = await service.DownloadAsync(info);

        try
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static UpdateInfo NewInfo(string prefix, long? size = 10)
    {
        var tag = $"{prefix}-{Guid.NewGuid():N}";
        return new UpdateInfo(new Version(9, 9), tag, "", "https://example.invalid/update", size);
    }

    private static string[] DownloadPaths(UpdateInfo info)
    {
        var dir = Path.Combine(Path.GetTempPath(), "InventoryTrackerUpdate");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, $"InventoryTracker-{info.TagName}-*.exe")
            : [];
    }

    private static void DeleteDownloadPaths(UpdateInfo info)
    {
        foreach (var path in DownloadPaths(info)) File.Delete(path);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public BlockingContent Body { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Body });
    }

    private sealed class BlockingContent : HttpContent
    {
        private readonly BlockingStream _stream = new();

        public TaskCompletionSource<CancellationToken> ReadStarted => _stream.ReadStarted;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length) { length = 10; return true; }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        public void Unblock() => _stream.Unblock();
    }

    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _unblocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CancellationToken> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 10;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public void Unblock() => _unblocked.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(cancellationToken);
            await _unblocked.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    private sealed class FixedHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            foreach (var timer in _timers.ToArray()) timer.Advance(elapsed);
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private TimeSpan _remaining = dueTime;
        private bool _disposed;
        public bool Change(TimeSpan newDueTime, TimeSpan period) { _remaining = newDueTime; return !_disposed; }
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Advance(TimeSpan elapsed)
        {
            if (_disposed || _remaining == Timeout.InfiniteTimeSpan) return;
            _remaining -= elapsed;
            if (_remaining <= TimeSpan.Zero)
            {
                _remaining = Timeout.InfiniteTimeSpan;
                callback(state);
            }
        }
    }
}
