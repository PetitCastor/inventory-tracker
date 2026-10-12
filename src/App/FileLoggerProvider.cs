using Microsoft.Extensions.Logging;

namespace InventoryTracker.App;

/// <summary>
/// Writes the tray app's log to <c>tracker.log</c> beside config.json. The app is a WinExe,
/// so the console logger has nowhere to go: without this file a failed or silent ingest pass
/// leaves no trace, and "the list did not update" cannot be told apart from "the logs were
/// never read".
/// <para>
/// One file is kept, rolled over to <c>tracker.log.1</c> past <see cref="MaxBytes"/>. Lines are
/// few — routine passes log at Debug, which the host filters out — so each is appended with
/// the file opened and closed around it, and nothing is left unflushed when the app exits.
/// </para>
/// </summary>
public sealed class FileLoggerProvider(string filePath) : ILoggerProvider
{
    public const long MaxBytes = 1024 * 1024;

    /// <summary>Beside config.json and the database: this install's private state.</summary>
    public static string DefaultPath => Path.Combine(
        Path.GetDirectoryName(AppConfig.DefaultPath) ?? ".", "tracker.log");

    private readonly Lock _gate = new();

    public string FilePath { get; } = filePath;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortName(categoryName));

    /// <summary>"InventoryTracker.Ingest.LogWatchService" reads as "LogWatchService".</summary>
    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    /// <summary>Appends one formatted entry. Never throws: logging must not be the thing
    /// that takes the tracker down.</summary>
    internal void Write(string entry)
    {
        lock (_gate)
        {
            try
            {
                if (Path.GetDirectoryName(FilePath) is { Length: > 0 } dir) Directory.CreateDirectory(dir);

                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);

                File.AppendAllText(FilePath, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked or read-only file costs the entry, not the app.
            }
        }
    }

    public void Dispose()
    {
    }
}
