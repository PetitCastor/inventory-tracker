using System.Globalization;
using InventoryTracker.Data;

namespace InventoryTracker.App;

/// <summary>
/// A snapshot of how well ingestion is keeping up, for the UI to surface without the user
/// having to run the CLI's stats block by hand.
/// <para>
/// <see cref="FilesWithLinesSinceInception"/> against <see cref="LogFiles"/> answers "is my
/// inception date cutting off files that actually have content?", and
/// <see cref="UnrecognisedMoveLines"/> answers "has the game's log grammar drifted out from
/// under the parser?" — see <see cref="Ingest.UnrecognisedMove"/>.
/// </para>
/// <para>
/// <see cref="FilesWithLinesSinceInception"/> counts sessions with <c>last_ts IS NOT NULL</c>,
/// not <c>first_ts</c>: <c>first_ts</c> is captured before the inception-date filter (so
/// <see cref="Ingest.LogIngestor.DiscardIfRotated"/> can identify a file regardless of where the
/// cutoff falls), so it is set for every file that has any line at all. <c>last_ts</c> is only
/// ever assigned to a line that passed the cutoff, so it stays the true signal for "this file
/// has content since my inception date" — using <c>first_ts</c> here would make the count equal
/// <see cref="LogFiles"/> always, and the "nothing after your inception date" banner could never
/// fire.
/// </para>
/// </summary>
public sealed record IngestHealth(
    string LogDir,
    DateTimeOffset? InceptionDate,
    int LogFiles,
    int FilesWithLinesSinceInception,
    long Moves,
    long UnrecognisedMoveLines,
    DateTimeOffset? NewestLogLine)
{
    public static readonly IngestHealth Empty = new(
        LogDir: "",
        InceptionDate: null,
        LogFiles: 0,
        FilesWithLinesSinceInception: 0,
        Moves: 0,
        UnrecognisedMoveLines: 0,
        NewestLogLine: null);

    public static IngestHealth Load(TrackerDb db, string logDir, DateTimeOffset? inceptionDate)
    {
        using var cn = db.Open();

        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                SUM(CASE WHEN last_ts IS NOT NULL THEN 1 ELSE 0 END),
                COALESCE(SUM(unrecognised), 0),
                MAX(last_line_ts)
            FROM session
            """;

        using var r = cmd.ExecuteReader();
        r.Read();

        var logFiles = r.GetInt32(0);
        var filesWithLines = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
        var unrecognised = r.GetInt64(2);
        var newestLogLine = r.IsDBNull(3)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture);

        using var movesCmd = cn.CreateCommand();
        movesCmd.CommandText = "SELECT COUNT(*) FROM move";
        var moves = Convert.ToInt64(movesCmd.ExecuteScalar());

        return new IngestHealth(logDir, inceptionDate, logFiles, filesWithLines, moves, unrecognised, newestLogLine);
    }
}
