using System.Globalization;
using System.Text.RegularExpressions;

namespace InventoryTracker.Ingest;

/// <summary>
/// The Star Citizen version behind each game build, as the RSI launcher names it.
/// <para>
/// Game.log only knows the build: its header says <c>Build(13165946)</c>, a
/// <c>FileVersion</c> of 4.10.200.58746 and a branch of <c>sc-alpha-4.10.0</c>, none of which
/// is the "4.10.3" the patch notes and the launcher show. The launcher writes that one into its
/// own log every time it installs or verifies the game:
/// <c>[Pipeline] Installing Star Citizen LIVE 4.10.3-live.13165946 at E:\Games\StarCitizen</c>.
/// </para>
/// <para>
/// The launcher rotates <c>log.log</c> into <c>log.old.log</c> at about 5 MB, so a build installed
/// long enough ago may no longer be named anywhere; callers fall back to the build number.
/// </para>
/// </summary>
public static partial class GameVersionCatalog
{
    /// <summary>Where the RSI launcher keeps its logs.</summary>
    public static string DefaultLauncherLogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rsilauncher", "logs");

    private static readonly string[] LogNames = ["log.old.log", "log.log"];

    // "4.10.3-live.13165946", "4.10.0-ptu.12519617": a dotted version, the channel, the build.
    [GeneratedRegex(
        @"\b(?<version>\d+\.\d+(?:\.\d+)?)-[a-z]+\.(?<build>\d{6,})\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionTag();

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, ((DateTime, long) Stamp, IReadOnlyDictionary<int, string> Map)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The version the launcher installed for <paramref name="build"/>, or null if it never said.</summary>
    public static string? VersionOf(int build, string? launcherLogDir = null) =>
        Load(launcherLogDir).GetValueOrDefault(build);

    /// <summary>
    /// "Star Citizen 4.10.3 (build 13165946)", or "Star Citizen build 13165946" when the launcher
    /// never named the build.
    /// </summary>
    public static string Describe(int build, string? launcherLogDir = null) =>
        VersionOf(build, launcherLogDir) is { } version
            ? $"Star Citizen {version} (build {build.ToString(CultureInfo.InvariantCulture)})"
            : $"Star Citizen build {build.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Every build the launcher logs name. Each log is read again only when it has changed: the
    /// header asks on every page render, the launcher appends to log.log while it is open, and
    /// the rotated log.old.log, 5 MB of it, never changes until the next rotation.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Load(string? launcherLogDir = null)
    {
        var dir = launcherLogDir ?? DefaultLauncherLogDir;
        var map = new Dictionary<int, string>();

        // Oldest first, so a build the newer log names again keeps its newer name.
        foreach (var path in LogNames.Select(n => Path.Combine(dir, n)))
        {
            foreach (var (build, version) in ReadLog(path)) map[build] = version;
        }

        return map;
    }

    /// <summary>What one launcher log names, from the cache while the file is unchanged.</summary>
    private static IReadOnlyDictionary<int, string> ReadLog(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return new Dictionary<int, string>();

        var stamp = (file.LastWriteTimeUtc, file.Length);
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Map;
        }

        var map = new Dictionary<int, string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                foreach (Match m in VersionTag().Matches(line))
                {
                    if (int.TryParse(m.Groups["build"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var build))
                    {
                        map[build] = m.Groups["version"].Value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The launcher is rotating it, or it is gone. The build number still stands on its
            // own; the next render tries again, so a partial read is not cached.
            return map;
        }

        lock (Gate)
        {
            Cache[path] = (stamp, map);
        }

        return map;
    }
}
