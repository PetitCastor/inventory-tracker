using System.Globalization;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.App;

/// <summary>One category's view of <see cref="FileLoggerProvider"/>. Level filtering is left
/// to the host's logging configuration, which runs before this is called.</summary>
internal sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        // Local time with its offset: the log is read next to the game's clock and the
        // player's own, and Game.log's UTC stamps can still be matched through the offset.
        var entry = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) +
                    $" {Label(logLevel)} {category}: " + formatter(state, exception);
        if (exception is not null) entry += Environment.NewLine + exception;

        provider.Write(entry + Environment.NewLine);
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "FATAL",
    };
}
