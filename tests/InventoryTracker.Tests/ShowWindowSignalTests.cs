using InventoryTracker.App;

namespace InventoryTracker.Tests;

public sealed class ShowWindowSignalTests
{
    private static string UniqueName() => $@"Local\InventoryTracker.Tests.{Guid.NewGuid():N}";

    [Fact]
    public void A_second_launch_reaches_the_running_instance()
    {
        var name = UniqueName();
        using var signal = new ShowWindowSignal(name);
        using var shown = new SemaphoreSlim(0);
        signal.Listen(() => shown.Release());

        Assert.True(ShowWindowSignal.TrySignal(name));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));

        // Auto-reset: every launch is one show, not a latch that stays set.
        Assert.True(ShowWindowSignal.TrySignal(name));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_signal_sent_before_the_window_listens_is_kept()
    {
        var name = UniqueName();
        using var signal = new ShowWindowSignal(name);
        Assert.True(ShowWindowSignal.TrySignal(name));

        using var shown = new SemaphoreSlim(0);
        signal.Listen(() => shown.Release());

        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Without_a_running_instance_the_caller_falls_back()
    {
        Assert.False(ShowWindowSignal.TrySignal(UniqueName()));
    }

    [Fact]
    public void Once_disposed_the_instance_no_longer_answers()
    {
        var name = UniqueName();
        new ShowWindowSignal(name).Dispose();

        Assert.False(ShowWindowSignal.TrySignal(name));
    }
}
