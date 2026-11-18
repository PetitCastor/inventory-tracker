namespace InventoryTracker.App;

/// <summary>One user-facing change in a release, such as "Put the station picker before search on Locations".</summary>
public sealed record ReleaseNoteEntry(ReleaseNoteKind Kind, string Text);
