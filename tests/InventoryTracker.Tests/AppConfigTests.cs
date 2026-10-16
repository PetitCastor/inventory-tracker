using InventoryTracker.App;
using System.Collections.Concurrent;

namespace InventoryTracker.Tests;

public sealed class AppConfigTests
{
    [Fact]
    public async Task Concurrent_saves_leave_one_complete_recoverable_configuration()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"itrk-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        using var start = new Barrier(3);
        using var writesReady = new Barrier(2);
        var tempPaths = new ConcurrentBag<string>();

        var first = new AppConfig
        {
            LogDir = Path.Combine(dir, "first-logs"),
            DatabasePath = Path.Combine(dir, "first.db"),
            Port = 5731,
            InceptionDate = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            SetupComplete = true,
        };
        var second = new AppConfig
        {
            LogDir = Path.Combine(dir, "second-logs"),
            DatabasePath = Path.Combine(dir, "second.db"),
            Port = 5732,
            InceptionDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            SetupComplete = false,
        };

        try
        {
            var saves = new[] { first, second }.Select(config => Task.Run(() =>
            {
                try
                {
                    if (!start.SignalAndWait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Concurrent save start barrier timed out.");

                    config.Save(path, tempPath =>
                    {
                        tempPaths.Add(tempPath);
                        if (!writesReady.SignalAndWait(TimeSpan.FromSeconds(2)))
                            throw new TimeoutException("Concurrent temp-write barrier timed out.");
                    });
                    return (Exception?)null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })).ToArray();

            var startReleased = start.SignalAndWait(TimeSpan.FromSeconds(5));
            var saveErrors = await Task.WhenAll(saves);
            Assert.True(startReleased, "Concurrent save start barrier timed out.");
            Assert.All(saveErrors, error => Assert.Null(error));

            var recovered = AppConfig.Load(path);
            Assert.True(IsSameConfiguration(first, recovered) || IsSameConfiguration(second, recovered));
            Assert.Equal(2, tempPaths.Count);
            Assert.Equal(2, tempPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool IsSameConfiguration(AppConfig expected, AppConfig actual) =>
        expected.LogDir == actual.LogDir
        && expected.DatabasePath == actual.DatabasePath
        && expected.Port == actual.Port
        && expected.InceptionDate == actual.InceptionDate
        && expected.SetupComplete == actual.SetupComplete;
}
