namespace LogParser.Ingest;

/// <summary>A Game.log or rotated backup, ordered for ingestion.</summary>
/// <param name="IsLive">True for the active Game.log, which is still being appended to.</param>
public sealed record LogFile(string Path, DateTime LastWrite, bool IsLive);

/// <summary>
/// Finds the game's logs. The live <c>Game.log</c> sits in the install root and the
/// rotated ones in <c>logbackups\</c>; both are needed for a full history.
/// </summary>
public static class LogFileLocator
{
    public const string DefaultLogDir = @"E:\Games\StarCitizen\LIVE";

    /// <summary>
    /// Log files worth parsing, oldest first. Files last written before
    /// <paramref name="cutoff"/> are dropped: pre-4.9 logs use an incompatible
    /// event vocabulary and would only produce noise.
    /// </summary>
    public static IReadOnlyList<LogFile> Discover(string logDir, DateTimeOffset cutoff)
    {
        var found = new List<LogFile>();

        var backups = Path.Combine(logDir, "logbackups");
        if (Directory.Exists(backups))
        {
            foreach (var path in Directory.EnumerateFiles(backups, "*.log"))
            {
                found.Add(new LogFile(path, File.GetLastWriteTimeUtc(path), IsLive: false));
            }
        }

        var live = Path.Combine(logDir, "Game.log");
        if (File.Exists(live))
        {
            found.Add(new LogFile(live, File.GetLastWriteTimeUtc(live), IsLive: true));
        }

        return found
            .Where(f => f.LastWrite >= cutoff.UtcDateTime)
            .OrderBy(f => f.LastWrite)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string LiveLogPath(string logDir) => Path.Combine(logDir, "Game.log");
}
