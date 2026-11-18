namespace InventoryTracker.App;

/// <summary>
/// Lets a second launch bring the running tracker's window to the front instead of starting a
/// copy that could not bind the port. The running instance owns a named event and waits on it;
/// a second launch sets it and exits.
/// </summary>
/// <remarks>
/// Session-scoped (<c>Local\</c>), like the single-instance mutex in <see cref="TrayHost"/>, so a
/// second desktop session gets its own tracker and its own window.
/// </remarks>
internal sealed class ShowWindowSignal : IDisposable
{
    public const string DefaultName = @"Local\InventoryTracker.ShowWindow";

    private readonly EventWaitHandle _event;
    private RegisteredWaitHandle? _wait;

    public ShowWindowSignal(string name = DefaultName) =>
        _event = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, name);

    /// <summary>
    /// Calls <paramref name="onSignaled"/> on a thread-pool thread each time another launch
    /// signals. A signal sent before this call is not lost: the event stays set until a waiter
    /// takes it, so the first wait completes at once.
    /// </summary>
    public void Listen(Action onSignaled)
    {
        if (_wait is not null) throw new InvalidOperationException("Already listening.");
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _event, (_, _) => onSignaled(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Asks the running instance to show its window. False when no instance owns the event:
    /// one still booting, or a build from before the window existed. The caller then falls
    /// back to opening the browser.
    /// </summary>
    public static bool TrySignal(string name = DefaultName)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle)) return false;
        using (handle) return handle.Set();
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event.Dispose();
    }
}
