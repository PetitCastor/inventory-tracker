using System.Windows.Forms;

namespace InventoryTracker.App;

/// <summary>
/// Small non-interactive window shown while a launch-time update downloads. There is no
/// Blazor UI to report into yet at this point — the web host has not started — so this is
/// plain WinForms, shown modelessly and pumped by hand from <see cref="TrayHost"/> since
/// <c>Application.Run</c> has not started its own message loop yet either.
/// </summary>
internal sealed class UpdateProgressForm : Form
{
    private readonly Label _status;
    private readonly ProgressBar _bar;

    public UpdateProgressForm(string versionLabel)
    {
        Text = "Inventory Tracker";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        ClientSize = new Size(360, 90);
        TopMost = true;

        _status = new Label
        {
            Text = $"Downloading update {versionLabel}…",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(16, 16),
            Size = new Size(328, 24),
        };

        _bar = new ProgressBar
        {
            Location = new Point(16, 46),
            Size = new Size(328, 20),
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Marquee,
        };

        Controls.Add(_status);
        Controls.Add(_bar);
    }

    public void SetProgress(double percent)
    {
        if (_bar.Style != ProgressBarStyle.Blocks) _bar.Style = ProgressBarStyle.Blocks;

        var clamped = Math.Clamp((int)percent, 0, 100);
        _bar.Value = clamped;
        _status.Text = $"Downloading update… {clamped}%";
    }

    public void SetStatus(string text) => _status.Text = text;
}
