using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using InventoryTracker.Components;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Naming;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.App;

/// <summary>
/// Runs the tracker as a tray application: a Blazor Server UI on loopback plus a
/// background service following Game.log, with a notify icon to reach both.
/// </summary>
public static class TrayHost
{
    private const string WikiClient = "star-citizen-wiki";

    /// <summary>Outbound identification to api.star-citizen.wiki. Shared with the CLI path.</summary>
    public const string WikiUserAgent = "InventoryTracker/1.0 (Star Citizen inventory tracker)";

    /// <summary>Session-scoped, so a second desktop session gets its own instance.</summary>
    private const string SingleInstanceMutex = @"Local\InventoryTracker.SingleInstance";

    public static int Run(string[] args)
    {
        var options = TrackerOptions.Load();

        // The port is fixed so the bookmark keeps working, which means a second instance
        // cannot bind and would die on app.Start() with no console to report it. Hand the
        // user the running instance's window instead.
        using var single = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var isFirstInstance);
        if (!isFirstInstance)
        {
            OpenBrowser(options.Url);
            return 0;
        }

        // A crash in an event handler or on a background thread would otherwise take the
        // tray app down silently: it is a WinExe, so there is no console for the trace.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(options.Url);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var db = new TrackerDb(options.DatabasePath);
        db.Initialize();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<TrackerState>();
        builder.Services.AddSingleton<DesktopShell>();

        // Registered by hand rather than as a typed client: the service caches the whole
        // catalogue in memory, so it has to be a singleton, and AddHttpClient<T> would
        // register it transient and quietly win over an AddSingleton for the same type.
        builder.Services.AddHttpClient(WikiClient, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(WikiUserAgent);
        });
        builder.Services.AddSingleton(sp => new WikiNameService(
            sp.GetRequiredService<TrackerDb>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(WikiClient)));

        builder.Services.AddSingleton<LogWatchService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LogWatchService>());

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();

        app.UseAntiforgery();

        // Served from the assembly rather than from wwwroot; see EmbedBlazorScript in
        // the csproj. Without this the pages prerender but never become interactive.
        app.MapGet("/_framework/blazor.web.js", () =>
        {
            var stream = typeof(TrayHost).Assembly.GetManifestResourceStream("blazor.web.js")
                ?? throw new InvalidOperationException("blazor.web.js was not embedded in the build.");

            return Results.Stream(stream, "text/javascript");
        });

        app.MapGet("/favicon.ico", () =>
        {
            var stream = typeof(TrayHost).Assembly.GetManifestResourceStream("app.ico")
                ?? throw new InvalidOperationException("app.ico was not embedded in the build.");

            return Results.Stream(stream, "image/x-icon");
        });

        app.MapRazorComponents<Root>().AddInteractiveServerRenderMode();

        // Kestrel runs alongside the WinForms message loop rather than owning the thread.
        try
        {
            app.Start();
        }
        catch (IOException ex)
        {
            // Almost always the port being held by something else — the single-instance
            // check above covers our own second copy, but not an unrelated listener.
            MessageBox.Show(
                $"Could not start the local UI on {options.Url}.\n\n{ex.Message}\n\n" +
                $"Another program may be using the port. Change it in {AppConfig.DefaultPath} and relaunch.",
                "Inventory Tracker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        using var tray = BuildTrayIcon(app, options);
        // Land first-time users on the setup wizard; the UI gate enforces it regardless.
        OpenBrowser(options.SetupComplete ? options.Url : $"{options.Url}/setup");
        Application.Run();

        app.StopAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static NotifyIcon BuildTrayIcon(WebApplication app, TrackerOptions options)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Open InventoryTracker", null, (_, _) => OpenBrowser(options.Url));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Rescan logs now", null, (_, _) =>
            app.Services.GetRequiredService<LogWatchService>().RequestScan());

        // The handler is async void — an EventHandler returns void — so nothing can observe
        // what it throws and an unhandled exception would end the process. RefreshAsync
        // makes ~62 network calls, so failure is routine rather than exceptional: being
        // offline must not cost the user their tracker.
        menu.Items.Add("Refresh item names from wiki", null, async (_, _) =>
        {
            try
            {
                var names = app.Services.GetRequiredService<WikiNameService>();
                await names.RefreshAsync();
                app.Services.GetRequiredService<TrackerState>().Rebuild();
            }
            catch (Exception ex)
            {
                app.Services.GetRequiredService<ILogger<WebApplication>>()
                    .LogWarning(ex, "Could not refresh item names");

                MessageBox.Show(
                    $"Could not download item names.\n\n{ex.Message}\n\n" +
                    "The tracker keeps working; items just show their raw class names.",
                    "Inventory Tracker",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        var icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "InventoryTracker",
            Visible = true,
            ContextMenuStrip = menu,
        };

        icon.DoubleClick += (_, _) => OpenBrowser(options.Url);
        return icon;
    }

    // Embedded alongside blazor.web.js so the tray icon survives single-file publish
    // rather than depending on a loose .ico sitting next to the exe.
    private static Icon LoadAppIcon()
    {
        using var stream = typeof(TrayHost).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser, or the association is broken. The tray icon still works,
            // and the URL is fixed, so this is recoverable by hand.
            MessageBox.Show(
                $"Could not open a browser.\n\n{ex.Message}\n\nOpen {url} manually.",
                "Inventory Tracker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void ReportFatal(Exception? ex)
    {
        MessageBox.Show(
            $"Inventory Tracker hit an unexpected error and has to close.\n\n{ex}",
            "Inventory Tracker",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        Application.Exit();
    }
}
