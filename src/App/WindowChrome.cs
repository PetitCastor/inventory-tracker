using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace InventoryTracker.App;

/// <summary>
/// The native parts of the window's look: whether Windows is in dark mode, and a dark or light
/// title bar to match the page under it.
/// </summary>
internal static class WindowChrome
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE. Windows 10 20H1 and later; older builds ignore it.
    private const int UseImmersiveDarkMode = 20;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // DllImport rather than LibraryImport, for the same reason as Program.AttachConsole: the
    // source generator would force AllowUnsafeBlocks on the whole project for one call.
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>True when Windows apps are set to dark. Light when the setting cannot be read,
    /// which is the Windows default.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="theme"/> draws dark right now.</summary>
    public static bool IsDark(AppTheme theme, bool systemDark) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => systemDark,
    };

    /// <summary>Gives the window a dark or light title bar. Best effort: a failure leaves the
    /// system default, which is only cosmetic.</summary>
    public static void ApplyTitleBar(IntPtr handle, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref value, sizeof(int));
    }
}
