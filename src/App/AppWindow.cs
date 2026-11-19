using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace InventoryTracker.App;

/// <summary>
/// The tracker's own window: a taskbar window whose whole client area is a WebView2 showing the
/// local Blazor UI. The UI server is unchanged, so the same pages still open in a browser from
/// the tray menu.
/// </summary>
/// <remarks>
/// Closing the window hides it to the tray (see <see cref="WindowCloseDecision"/>): the tracker
/// has to keep following Game.log while the game runs. When the WebView2 runtime is missing or
/// fails to start, the window stays hidden and the UI opens in the default browser instead, as
/// it did before this window existed; <see cref="ShowAndActivate"/> keeps doing that afterwards.
/// <para>
/// UI/interop edge with no unit tests of its own: it needs a desktop and a WebView2 runtime.
/// The decisions it makes live in <see cref="WindowCloseDecision"/>,
/// <see cref="AppNavigationPolicy"/> and <see cref="WindowChrome.IsDark"/>, which are tested.
/// </para>
/// </remarks>
internal sealed class AppWindow : Form
{
    // The page background of each palette (AppStyles.razor --bg), painted until the page loads
    // so the window never flashes white under a dark UI.
    private static readonly Color DarkBackground = Color.FromArgb(0x0E, 0x11, 0x16);
    private static readonly Color LightBackground = Color.FromArgb(0xF6, 0xF8, 0xFA);

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly TrackerOptions _options;
    private readonly Uri _appOrigin;
    private readonly string _startUrl;
    private readonly string _userDataFolder;
    private readonly Action<string> _openInBrowser;
    private readonly Action _requestExit;
    private readonly ILogger _log;

    /// <param name="options">The theme is read from here and followed when Settings changes it.</param>
    /// <param name="startUrl">The first page: the setup wizard on a first run, otherwise the app root.</param>
    /// <param name="userDataFolder">WebView2's profile folder, beside the tracker's other private state.</param>
    /// <param name="icon">The caption and taskbar icon.</param>
    /// <param name="openInBrowser">Opens a URL in the default browser.</param>
    /// <param name="requestExit">Stops the tracker; called when Windows closes the window for good.</param>
    /// <param name="log">Where web view failures are written.</param>
    public AppWindow(
        TrackerOptions options,
        string startUrl,
        string userDataFolder,
        Icon icon,
        Action<string> openInBrowser,
        Action requestExit,
        ILogger log)
    {
        _options = options;
        _appOrigin = new Uri(options.Url);
        _startUrl = startUrl;
        _userDataFolder = userDataFolder;
        _openInBrowser = openInBrowser;
        _requestExit = requestExit;
        _log = log;

        Text = "Inventory Tracker";
        Icon = icon;
        ShowInTaskbar = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // Sized for the item grid rather than a full desktop: the pages stretch to whatever
        // the user resizes it to, and below 760 px wide they switch to their narrow layout.
        MinimumSize = new Size(760, 520);
        Size = new Size(1100, 760);
        StartPosition = FormStartPosition.CenterScreen;

        Controls.Add(_webView);

        HandleCreated += (_, _) => ApplyTheme();
        Load += async (_, _) => await InitializeWebViewAsync();
        _options.ThemeChanged += OnThemeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplyBackground();
    }

    /// <summary>True once the web view could not start; the UI is then reached in the browser.</summary>
    public bool WebViewFailed { get; private set; }

    /// <summary>
    /// Restores, shows and focuses the window, or opens the browser when the web view could not
    /// start. UI thread only: the tray menu calls it there, and a second launch's signal is
    /// posted to the message loop by <see cref="TrayHost"/>.
    /// </summary>
    public void ShowAndActivate()
    {
        if (IsDisposed) return;

        if (WebViewFailed)
        {
            _openInBrowser(_appOrigin.ToString());
            return;
        }

        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ShowOnScreen();
        Activate();
    }

    /// <summary>
    /// Shows the window, and makes sure Windows did too.
    /// <para>
    /// A process launched with a show state in its startup info (STARTF_USESHOWWINDOW) gets that
    /// state applied to the first window it shows, whatever the call asks for. Up to 0.2.49 the
    /// updater launched the new version that way, hidden, and the 0.2.49 updater is still what
    /// launches the next one. <see cref="Form.Show"/> then leaves <see cref="Control.Visible"/>
    /// true over a hidden window, so every later Show is a no-op. The second ShowWindow call is
    /// honoured as asked, which is what brings it back.
    /// </para>
    /// </summary>
    public void ShowOnScreen()
    {
        Show();
        if (!IsWindowVisible(Handle)) ShowWindow(Handle, ShowNormal);
    }

    private const int ShowNormal = 1;

    // DllImport rather than LibraryImport, as in Program: the source generator would force
    // AllowUnsafeBlocks on the whole project for two calls.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        switch (WindowCloseDecision.Decide(e.CloseReason))
        {
            case WindowCloseAction.HideToTray:
                e.Cancel = true;
                Hide();
                return;
            case WindowCloseAction.CloseAndExit:
                _requestExit();
                break;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _options.ThemeChanged -= OnThemeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        base.Dispose(disposing);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;

            // A target="_blank" link or window.open never grows a WebView2 popup. Links out of
            // the app go to the default browser, and only when the user clicked: a script
            // calling window.open in a loop cannot open browser tabs.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                var target = ToUri(e.Uri);
                if (AppNavigationPolicy.IsAppPage(target, _appOrigin))
                    core.Navigate(target!.ToString());
                else if (e.IsUserInitiated && AppNavigationPolicy.MayOpenExternally(target, _appOrigin))
                    _openInBrowser(target!.ToString());
            };

            // The window itself never leaves the tracker's pages; a clicked outside link still
            // reaches the browser.
            core.NavigationStarting += (_, e) =>
            {
                var target = ToUri(e.Uri);
                if (AppNavigationPolicy.IsAppPage(target, _appOrigin)) return;

                e.Cancel = true;
                if (e.IsUserInitiated && AppNavigationPolicy.MayOpenExternally(target, _appOrigin))
                    _openInBrowser(target!.ToString());
            };

            // A crashed page process leaves a blank window; reloading brings the UI back, and
            // the Blazor circuit reconnects on its own.
            core.ProcessFailed += (_, e) =>
            {
                _log.LogWarning("App window: WebView2 {Kind} ({Reason})", e.ProcessFailedKind, e.Reason);
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                    RunOnUiThread(() => _webView.Reload());
            };

            ApplyTheme();
            core.Navigate(_startUrl);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // The runtime missing on an old Windows 10 build is the expected case, but whatever
            // the cause, the tracker keeps running and its UI stays reachable in the browser.
            _log.LogWarning(ex, "App window: WebView2 could not start; opening the browser instead");
            WebViewFailed = true;
            _openInBrowser(_startUrl);
            RunOnUiThread(Hide);
        }
    }

    private void OnThemeChanged(AppTheme theme) => RunOnUiThread(ApplyTheme);

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Only System follows Windows, and the light/dark switch arrives as General.
        if (e.Category == UserPreferenceCategory.General && _options.Theme == AppTheme.System)
            RunOnUiThread(ApplyTheme);
    }

    private bool IsEffectivelyDark() => WindowChrome.IsDark(_options.Theme, WindowChrome.IsSystemDark());

    private void ApplyTheme()
    {
        if (IsDisposed) return;

        ApplyBackground();
        if (IsHandleCreated) WindowChrome.ApplyTitleBar(Handle, IsEffectivelyDark());

        // prefers-color-scheme inside the page follows this, which is what the System theme's
        // CSS reads; Light and Dark set the page's own data-theme as well.
        if (_webView.CoreWebView2 is { } core)
        {
            core.Profile.PreferredColorScheme = _options.Theme switch
            {
                AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
                AppTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
        }
    }

    private void ApplyBackground()
    {
        var background = IsEffectivelyDark() ? DarkBackground : LightBackground;
        BackColor = background;
        _webView.DefaultBackgroundColor = background;
    }

    private static Uri? ToUri(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed) return;

        try
        {
            if (IsHandleCreated) BeginInvoke(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing; there is nothing left to update.
        }
    }
}
