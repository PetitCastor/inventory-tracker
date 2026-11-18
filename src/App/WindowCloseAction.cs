namespace InventoryTracker.App;

/// <summary>What <see cref="AppWindow"/> does with a close request; see <see cref="WindowCloseDecision"/>.</summary>
internal enum WindowCloseAction
{
    /// <summary>Cancel the close and hide the window: the tracker keeps running in the tray.</summary>
    HideToTray,

    /// <summary>Let the window close; an exit is already under way.</summary>
    Close,

    /// <summary>Let the window close and stop the tracker, since Windows is ending the process.</summary>
    CloseAndExit,
}
