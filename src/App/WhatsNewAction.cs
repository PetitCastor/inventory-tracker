namespace InventoryTracker.App;

/// <summary>The outcome of <see cref="WhatsNewDecision.Decide"/>.</summary>
public enum WhatsNewAction
{
    /// <summary>This version was already announced, or is older than the one that was.</summary>
    Nothing,

    /// <summary>Remember this version without showing anything: a fresh install has no news.</summary>
    Record,

    /// <summary>Show the notes since the version last seen.</summary>
    Show,
}
