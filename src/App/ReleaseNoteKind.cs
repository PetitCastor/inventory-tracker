namespace InventoryTracker.App;

/// <summary>What a release note line announces, from its Conventional Commits type.</summary>
public enum ReleaseNoteKind
{
    /// <summary><c>feat:</c></summary>
    New,

    /// <summary><c>perf:</c></summary>
    Improved,

    /// <summary><c>fix:</c></summary>
    Fixed,
}
