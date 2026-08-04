using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InventoryTracker.Ingest;

/// <summary>
/// One structured Game.log line:
/// <c>&lt;2026-07-16T11:20:52.355Z&gt; [Notice] &lt;EventName&gt; rest of line...</c>
/// </summary>
public sealed partial class LogLine
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Severity { get; init; }
    public required string EventName { get; init; }
    public required string Rest { get; init; }
    public required string Raw { get; init; }

    // Event names are not word-safe: they contain spaces, "::", and even "<lambda_1".
    // Only the outer angle brackets are reliable, so match non-greedily up to the first '>'.
    [GeneratedRegex(
        @"^<(?<ts>[^>]+)> \[(?<sev>\w+)\] <(?<ev>[^>]*)>\s*(?<rest>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    public static bool TryParse(string raw, [NotNullWhen(true)] out LogLine? line)
    {
        line = null;
        if (string.IsNullOrEmpty(raw) || raw[0] != '<') return false;

        var m = Shape().Match(raw);
        if (!m.Success) return false;

        if (!DateTimeOffset.TryParse(
                m.Groups["ts"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var ts))
        {
            return false;
        }

        line = new LogLine
        {
            Timestamp = ts,
            Severity = m.Groups["sev"].Value,
            EventName = m.Groups["ev"].Value,
            Rest = m.Groups["rest"].Value,
            Raw = raw,
        };
        return true;
    }
}
