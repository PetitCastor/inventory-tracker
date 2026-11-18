using System.Text.RegularExpressions;

namespace InventoryTracker.App;

/// <summary>
/// Turns a GitHub release body into user-facing notes. CI writes each release's body from the
/// squash commit that shipped it, one Conventional Commits subject per line, such as
/// <c>feat: put the station picker before search on Locations (#57)</c>.
/// </summary>
/// <remarks>
/// Only <c>feat</c>, <c>perf</c> and <c>fix</c> lines reach the user. Everything else (<c>ci</c>,
/// <c>docs</c>, <c>test</c>, <c>chore</c>, dependency bumps, free text) is development history
/// and is left out, so a release made only of those has nothing to announce.
/// </remarks>
public static partial class ReleaseNoteParser
{
    /// <summary>The notes in <paramref name="body"/>, in the order written.</summary>
    public static IReadOnlyList<ReleaseNoteEntry> ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];

        var entries = new List<ReleaseNoteEntry>();
        foreach (var rawLine in body.Split('\n'))
        {
            // A BOM has reached a squash subject before (an editor saved the commit message with
            // one), and markdown bullets are how a hand-edited body would list the same lines.
            var line = rawLine.Trim().TrimStart('\uFEFF').Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                line = line[2..].TrimStart();

            var match = SubjectPattern().Match(line);
            if (!match.Success) continue;

            ReleaseNoteKind? kind = match.Groups["type"].Value.ToLowerInvariant() switch
            {
                "feat" => ReleaseNoteKind.New,
                "perf" => ReleaseNoteKind.Improved,
                "fix" => ReleaseNoteKind.Fixed,
                _ => null,
            };
            if (kind is null) continue;

            var text = PullRequestSuffix().Replace(match.Groups["text"].Value, "").Trim();
            if (text.Length == 0) continue;

            entries.Add(new ReleaseNoteEntry(kind.Value, char.ToUpperInvariant(text[0]) + text[1..]));
        }

        return entries;
    }

    /// <summary>Reads a tag such as <c>v0.2.45</c>; the result is normalised to four components.</summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var parsed)) return false;

        version = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// Fills unset components with zero. <see cref="System.Version"/> treats an unset component as
    /// lower than zero, so without this <c>0.2.45</c> would sort below the assembly's own <c>0.2.45.0</c>.
    /// </summary>
    public static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    /// <summary>How a version is shown and stored: three components, four for a test build.</summary>
    public static string Format(Version version)
    {
        var normal = Normalize(version);
        return normal.Revision > 0 ? normal.ToString(4) : normal.ToString(3);
    }

    // type, optional (scope), optional breaking "!", colon, text.
    [GeneratedRegex(@"^(?<type>[A-Za-z]+)(\([^)]*\))?!?:\s*(?<text>.+)$")]
    private static partial Regex SubjectPattern();

    // The " (#57)" GitHub appends to a squash subject.
    [GeneratedRegex(@"\s*\(#\d+\)\s*$")]
    private static partial Regex PullRequestSuffix();
}
