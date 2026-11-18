using System.Windows.Forms;
using InventoryTracker.App;

namespace InventoryTracker.Tests;

/// <summary>The app window's decisions, tested without a window or a WebView2 runtime.</summary>
public sealed class AppWindowDecisionsTests
{
    private static readonly Uri App = new("http://localhost:5730");

    [Fact]
    public void Close_button_hides_the_window_to_the_tray()
    {
        Assert.Equal(WindowCloseAction.HideToTray, WindowCloseDecision.Decide(CloseReason.UserClosing));
    }

    [Theory]
    [InlineData(CloseReason.WindowsShutDown)]
    [InlineData(CloseReason.TaskManagerClosing)]
    public void Windows_closing_the_window_also_stops_the_tracker(CloseReason reason)
    {
        Assert.Equal(WindowCloseAction.CloseAndExit, WindowCloseDecision.Decide(reason));
    }

    [Theory]
    [InlineData(CloseReason.ApplicationExitCall)]
    [InlineData(CloseReason.None)]
    [InlineData(CloseReason.FormOwnerClosing)]
    public void A_close_the_tracker_asked_for_is_let_through(CloseReason reason)
    {
        Assert.Equal(WindowCloseAction.Close, WindowCloseDecision.Decide(reason));
    }

    [Theory]
    [InlineData("http://localhost:5730/")]
    [InlineData("http://localhost:5730/settings?tab=updates")]
    [InlineData("http://LOCALHOST:5730/ships")]
    public void The_trackers_own_pages_stay_in_the_window(string url)
    {
        Assert.True(AppNavigationPolicy.IsAppPage(new Uri(url), App));
        Assert.False(AppNavigationPolicy.MayOpenExternally(new Uri(url), App));
    }

    [Theory]
    [InlineData("http://localhost:5731/")]
    [InlineData("https://localhost:5730/")]
    [InlineData("http://127.0.0.1:5730/")]
    [InlineData("https://starcitizen.tools/Main_Page")]
    public void Other_origins_never_load_in_the_window(string url)
    {
        Assert.False(AppNavigationPolicy.IsAppPage(new Uri(url), App));
    }

    [Theory]
    [InlineData("https://starcitizen.tools/Main_Page")]
    [InlineData("http://example.com/")]
    public void Clicked_web_links_go_to_the_browser(string url)
    {
        Assert.True(AppNavigationPolicy.MayOpenExternally(new Uri(url), App));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    public void Other_schemes_are_never_handed_to_the_shell(string url)
    {
        Assert.False(AppNavigationPolicy.MayOpenExternally(new Uri(url), App));
        Assert.False(AppNavigationPolicy.IsAppPage(new Uri(url), App));
    }

    [Fact]
    public void A_missing_address_goes_nowhere()
    {
        Assert.False(AppNavigationPolicy.IsAppPage(null, App));
        Assert.False(AppNavigationPolicy.MayOpenExternally(null, App));
    }

    [Theory]
    [InlineData(AppTheme.Dark, false, true)]
    [InlineData(AppTheme.Dark, true, true)]
    [InlineData(AppTheme.Light, true, false)]
    [InlineData(AppTheme.Light, false, false)]
    [InlineData(AppTheme.System, true, true)]
    [InlineData(AppTheme.System, false, false)]
    public void Only_the_system_theme_follows_windows(AppTheme theme, bool systemDark, bool expectedDark)
    {
        Assert.Equal(expectedDark, WindowChrome.IsDark(theme, systemDark));
    }
}
