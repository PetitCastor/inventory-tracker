using System.Windows.Forms;

namespace InventoryTracker.App;

/// <summary>
/// The small amount of native desktop interaction the Blazor UI needs: showing a folder
/// picker. Registered as a singleton so pages can inject it, and safe to call from a Blazor
/// circuit thread even though the app's message loop lives on the WinForms UI thread.
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

            // The user is looking at a browser, and this thread owns no window. Without a
            // topmost owner the dialog can open behind everything, leaving the page looking
            // frozen with nothing on screen to explain why — and the request blocked until
            // a dialog the user cannot see is dismissed.
            using var owner = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
                Size = new System.Drawing.Size(1, 1),
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
            };
            owner.Show();

            if (dialog.ShowDialog(owner) == DialogResult.OK)
                result = dialog.SelectedPath;

            owner.Close();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return result;
    }
}
