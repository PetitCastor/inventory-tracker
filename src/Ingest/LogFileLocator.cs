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
    /// <summary>The channel folders the launcher creates, most likely first.</summary>
    private static readonly string[] Channels = ["LIVE", "PTU", "EPTU"];

    /// <summary>
    /// Paths relative to a drive root or Program Files where the RSI launcher installs.
    /// The default is the first that exists, so ordering is the preference order.
    /// </summary>
    private static readonly string[] InstallRoots =
    [
        @"Roberts Space Industries\StarCitizen",
        @"Games\StarCitizen",
        @"StarCitizen",
    ];

    /// <summary>
    /// A best guess at the install, probed once. Empty when nothing is found, which leaves
    /// the setup wizard's folder field blank rather than pre-filling a path that does not
    /// exist on this machine.
    /// <para>
    /// Declared after the arrays it reads on purpose: static field initialisers run in
    /// declaration order, so probing from above them would read nulls.
    /// </para>
    /// </summary>
    public static string DefaultLogDir { get; } = ProbeLogDir();

    /// <summary>
    /// Channel is the outermost loop on purpose: a LIVE install on any drive beats a PTU one,
    /// and players routinely have both on different disks. Looping drives first would hand
    /// back whichever happened to sort earlier, which is how a D:\...\PTU install can win
    /// over the E:\...\LIVE the player actually plays.
    /// </summary>
    private static string ProbeLogDir()
    {
        foreach (var channel in Channels)
        {
            foreach (var root in CandidateRoots())
            {
                foreach (var install in InstallRoots)
                {
                    var candidate = Path.Combine(root, install, channel);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
        }

        return "";
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // The game is large and routinely lives on a secondary drive rather than under
        // Program Files, so every fixed drive is worth a look. Ready drives only: probing
        // a disconnected network or removable drive blocks for its timeout.
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            yield return drive.RootDirectory.FullName;
        }
    }

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

    /// <summary>The first parseable line's timestamp, used to tell one Game.log from the
    /// next after a rotation. Null when the file has no parseable line near its head.</summary>
    public static DateTimeOffset? FirstTimestamp(string path)
    {
        try
        {
            var scanned = 0;
            foreach (var line in new ByteLineReader().ReadLines(path))
            {
                if (LogLine.TryParse(line.Text, out var log)) return log.Timestamp;

                // The header is a handful of lines; anything further means this is not a
                // log we can date, and reading the whole file to find that out is wasteful.
                if (++scanned > HeadScanLines) break;
            }
        }
        catch (IOException)
        {
            // The game rotates the file out from under us; the next pass tries again.
        }

        return null;
    }

    private const int HeadScanLines = 200;

    /// <summary>
    /// The game build a log was written by: from a rotated backup's file name, or from the
    /// live Game.log's own header, whose first line names the backup it will become
    /// (<c>BackupNameAttachment=" Build(12660092) 26 Aug 26 ..."</c>). Null when neither says.
    /// </summary>
    public static int? BuildOf(string path)
    {
        var m = BuildId().Match(Path.GetFileName(path));
        if (m.Success && int.TryParse(m.Groups["build"].Value, out var fromName)) return fromName;

        try
        {
            var scanned = 0;
            foreach (var line in new ByteLineReader().ReadLines(path))
            {
                var h = BuildId().Match(line.Text);
                if (h.Success && int.TryParse(h.Groups["build"].Value, out var fromHeader)) return fromHeader;
                if (++scanned > HeaderBuildLines) break;
            }
        }
        catch (IOException)
        {
            // Rotated out from under us; the next pass tries again.
        }

        return null;
    }

    /// <summary>The build is on the first line; a few more cover a header that grows.</summary>
    private const int HeaderBuildLines = 20;

    private static bool IsParseable(string path, DateTimeOffset cutoff)
    {
        var m = BuildId().Match(Path.GetFileName(path));
        if (m.Success && int.TryParse(m.Groups["build"].Value, out var build)) return build >= MinBuild;

        return File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime;
    }
}
