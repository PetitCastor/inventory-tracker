using InventoryTracker.App;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_dir, "logs", "tracker.log");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void An_entry_lands_in_the_file_with_its_level_short_category_and_exception()
    {
        using var provider = new FileLoggerProvider(LogPath);
        var log = provider.CreateLogger("InventoryTracker.Ingest.LogWatchService");

        log.LogWarning(new IOException("sharing violation"), "Logs unreadable for {Count} passes", 3);

        var text = File.ReadAllText(LogPath);
        Assert.Contains("WARN  LogWatchService: Logs unreadable for 3 passes", text);
        Assert.Contains("System.IO.IOException: sharing violation", text);
    }

    [Fact]
    public void The_file_rolls_over_once_it_passes_the_size_cap()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, new string('x', (int)FileLoggerProvider.MaxBytes + 1));

        using var provider = new FileLoggerProvider(LogPath);
        provider.CreateLogger("Test").LogInformation("fresh");

        Assert.True(File.Exists(LogPath + ".1"));
        Assert.Contains("INFO  Test: fresh", File.ReadAllText(LogPath));
        Assert.DoesNotContain("xxx", File.ReadAllText(LogPath));
    }
}
