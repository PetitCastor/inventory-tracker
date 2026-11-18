using System.Windows.Forms;

namespace InventoryTracker.App;

/// <summary>
/// Close handling for <see cref="AppWindow"/>, kept out of the form so it can be tested without
/// a window or a WebView2 runtime.
/// </summary>
internal static class WindowCloseDecision
{
    /// <summary>
    /// The window's own close button hides it, because the tracker has to keep following
    /// Game.log while the game runs; Exit in the tray menu is the way to quit. A close the
    /// tracker asked for itself (<see cref="Application.Exit()"/>, or the message loop stopping
    /// for an update restart) is let through. A close Windows forces on it (sign-out, shutdown,
    /// Task Manager) is let through and also stops the tracker, so the database and the log
    /// follower shut down cleanly instead of being cut off with the window gone.
    /// </summary>
    public static WindowCloseAction Decide(CloseReason reason) => reason switch
    {
        CloseReason.UserClosing => WindowCloseAction.HideToTray,
        CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing => WindowCloseAction.CloseAndExit,
        _ => WindowCloseAction.Close,
    };
}
