namespace InventoryTracker.App;

/// <summary>
/// One GitHub release and the user-facing changes it lists. <see cref="Version"/> always has four
/// components (see <see cref="ReleaseNoteParser.Normalize"/>), so it compares correctly with a
/// four-part test build.
/// </summary>
public sealed record ReleaseNote(
    Version Version,
    string TagName,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseNoteEntry> Entries);
