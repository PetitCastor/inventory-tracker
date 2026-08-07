using System.Text.RegularExpressions;

namespace InventoryTracker.Ingest;

/// <summary>A Game.log or rotated backup, ordered for ingestion.</summary>
/// <param name="IsLive">True for the active Game.log, which is still being appended to.</param>
public sealed record LogFile(string Path, DateTime LastWrite, bool IsLive);

/// <summary>
/// Finds the game's logs. The live <c>Game.log</c> sits in the install root and the
/// rotated ones in <c>logbackups\</c>; both are needed for a full history.
/// </summary>
public static partial class LogFileLocator
{
    public const string DefaultLogDir = @"E:\Games\StarCitizen\LIVE";

    /// <summary>
    /// First 4.9 build. Earlier clients use a different inventory event vocabulary, so their
    /// logs would parse into noise rather than into nothing.
    /// </summary>
    public const int MinBuild = 12232306;

    // Rotated backups are named "Game Build(12344265) 31 Jul 26 (21 28 10).log".
    [GeneratedRegex(@"Build\((?<build>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex BuildId();

    /// <summary>
    /// Log files worth parsing, oldest first.
    /// <para>
    /// Selection is by the build id in the filename, not by file timestamp: a backup that
    /// gets touched by a restore, sync or antivirus pass would otherwise be admitted and
    /// parsed under rules its client never used, and a genuine 4.9 file with an old mtime
    /// would be dropped. Files with no build id in the name fall back to
    /// <paramref name="cutoff"/>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LogFile> Discover(string logDir, DateTimeOffset cutoff)
    {
        var found = new List<LogFile>();

        var backups = Path.Combine(logDir, "logbackups");
        if (Directory.Exists(backups))
        {
            foreach (var path in Directory.EnumerateFiles(backups, "*.log"))
            {
                if (!IsParseable(path, cutoff)) continue;
                found.Add(new LogFile(path, File.GetLastWriteTimeUtc(path), IsLive: false));
            }
        }

        // The live log is always current by definition, and its name carries no build id.
        var live = Path.Combine(logDir, "Game.log");
        if (File.Exists(live))
        {
            found.Add(new LogFile(live, File.GetLastWriteTimeUtc(live), IsLive: true));
        }

        return found
            .OrderBy(f => f.LastWrite)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsParseable(string path, DateTimeOffset cutoff)
    {
        var m = BuildId().Match(Path.GetFileName(path));
        if (m.Success && int.TryParse(m.Groups["build"].Value, out var build)) return build >= MinBuild;

        return File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime;
    }

    public static string LiveLogPath(string logDir) => Path.Combine(logDir, "Game.log");
}
