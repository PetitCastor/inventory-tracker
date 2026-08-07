using System.Windows.Forms;

namespace InventoryTracker.App;

/// <summary>
/// The small amount of native desktop interaction the Blazor UI needs: showing a folder
/// picker and revealing a folder in Explorer. Registered as a singleton so pages can inject
/// it. Both methods are safe to call from a Blazor circuit thread even though the app's
/// message loop lives on the WinForms UI thread.
/// </summary>
public sealed class DesktopShell
{
    /// <summary>
    /// Shows a native folder-browse dialog and returns the chosen path, or null if the user
    /// cancelled. <see cref="FolderBrowserDialog"/> requires an STA thread, so it runs on a
    /// throwaway one rather than borrowing the Kestrel/circuit thread it is called from.
    /// </summary>
    public string? PickFolder(string? initial)
    {
        string? result = null;

        var thread = new Thread(() =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select your Star Citizen LIVE folder (the one containing Game.log)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };

            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                dialog.SelectedPath = initial;

            if (dialog.ShowDialog() == DialogResult.OK)
                result = dialog.SelectedPath;
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return result;
    }
}
