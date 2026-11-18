namespace InventoryTracker.App;

/// <summary>
/// Which addresses the app window may show itself, and which ones go to the default browser.
/// The window only ever shows the tracker's own pages; anything else the page links to opens
/// outside it, so the window never turns into a general-purpose browser.
/// </summary>
internal static class AppNavigationPolicy
{
    /// <summary>
    /// True for a page of the tracker's own local UI: same scheme, host and port as
    /// <paramref name="appOrigin"/>. Hosts compare case-insensitively, as URLs do.
    /// </summary>
    public static bool IsAppPage(Uri? target, Uri appOrigin) =>
        target is { IsAbsoluteUri: true }
        && string.Equals(target.Scheme, appOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(target.Host, appOrigin.Host, StringComparison.OrdinalIgnoreCase)
        && target.Port == appOrigin.Port;

    /// <summary>
    /// True when a link the user clicked may be handed to the default browser: an http or https
    /// address outside the app. Other schemes (file:, javascript:, custom protocol handlers)
    /// never leave the window, since starting one launches whatever program claims it.
    /// </summary>
    public static bool MayOpenExternally(Uri? target, Uri appOrigin) =>
        target is { IsAbsoluteUri: true }
        && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp)
        && !IsAppPage(target, appOrigin);
}
