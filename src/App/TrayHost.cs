using System.Diagnostics;
using System.Drawing;
using System.Threading;
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
    private const string UpdateClient = "github-releases";

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

        // Checked before anything else starts: if a newer build exists, this replaces the
        // running exe and relaunches it instead of continuing to boot the old one.
        if (CheckAndApplyUpdateOnStartup())
        {
            return 0;
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(options.Url);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // A WinExe has no console, so this file is the only place the log ends up. The
        // framework's own Information lines (one per HTTP request) would bury the tracker's.
        builder.Logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultPath));
        builder.Logging.AddFilter<FileLoggerProvider>("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter<FileLoggerProvider>("System", LogLevel.Warning);

        // Exit has nothing to flush (every write is already committed), but the default 30 s
        // wait for open browser circuits to drain makes it look hung.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));

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
        builder.Services.AddSingleton(sp => new PlaceNameService(
            sp.GetRequiredService<TrackerDb>(),
            sp.GetRequiredService<TrackerOptions>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(WikiClient)));

        // Same registration shape as the wiki client above, for the "Check for updates"
        // button in Settings — the startup check in Run() uses its own short-lived
        // HttpClient instead, since it runs before this container exists.
        //
        // With ResponseHeadersRead, HttpClient.Timeout only bounds the request through its
        // headers. UpdateService applies its own five-minute token to the download body; the
        // check step below uses the client's timeout while reading the release JSON.
        builder.Services.AddHttpClient(UpdateClient, c =>
        {
            c.Timeout = TimeSpan.FromMinutes(5);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateService.UserAgent);
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });
        builder.Services.AddSingleton(sp => new UpdateService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(UpdateClient)));

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

        // Under the app's own category: ILogger<WebApplication> logs as Microsoft.AspNetCore.*,
        // which the file filter above holds to Warning, so this line never reached tracker.log.
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TrayHost).FullName!).LogInformation(
            "InventoryTracker {Version} started on {Url}, reading {LogDir}",
            UpdateService.CurrentVersion, options.Url, options.LogDir);

        var tray = BuildTrayIcon(app, options);
        try
        {
            // Land first-time users on the setup wizard; the UI gate enforces it regardless.
            OpenBrowser(options.SetupComplete ? options.Url : $"{options.Url}/setup");
            Application.Run();
        }
        finally
        {
            // Gone before the host stops: an open browser tab holds a Blazor circuit that
            // Kestrel waits on, and the icon should not linger in the tray while it does.
            tray.Visible = false;
            tray.Dispose();
        }

        try
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The shutdown timeout ran out on a circuit or a pass still winding down. The
            // process is exiting anyway; a crash dialog after "Exit" would only be noise.
        }

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
        // what it throws and an unhandled exception would end the process. The two refreshes
        // make ~72 network calls, so failure is routine rather than exceptional: being
        // offline must not cost the user their tracker.
        menu.Items.Add("Refresh names from wiki", null, async (_, _) =>
        {
            try
            {
                // Each download is kept on its own: a failed place list must not cost the
                // item names that did arrive, nor skip the rebuild that shows them.
                Exception? failed = null;
                try { await app.Services.GetRequiredService<WikiNameService>().RefreshAsync(); }
                catch (Exception ex) { failed = ex; }

                try
                {
                    await app.Services.GetRequiredService<PlaceNameService>().RefreshAsync();
                    app.Services.GetRequiredService<LogWatchService>().RequestPlaceResolution();
                }
                catch (Exception ex) { failed ??= ex; }

                app.Services.GetRequiredService<TrackerState>().Rebuild();
                if (failed is not null) throw failed;
            }
            catch (Exception ex)
            {
                app.Services.GetRequiredService<ILogger<WebApplication>>()
                    .LogWarning(ex, "Could not refresh names from the wiki");

                MessageBox.Show(
                    $"Could not download names from the wiki.\n\n{ex.Message}\n\n" +
                    "The tracker keeps working; items show their raw class names, and " +
                    "place names stay unconfirmed until you confirm them.",
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

    /// <summary>
    /// Checks GitHub for a newer release and, if found, downloads it and hands off to the
    /// updater helper — returning true, meaning the caller should stop and let the new
    /// process take over. Runs before the DI container exists, so it uses its own
    /// short-lived HttpClient rather than <see cref="UpdateClient"/>.
    /// <para>
    /// Anything that goes wrong here — offline, GitHub unreachable, a bad download — is
    /// swallowed: a failed update check must never stop the tracker itself from starting.
    /// </para>
    /// </summary>
    private static bool CheckAndApplyUpdateOnStartup()
    {
        try
        {
            // The client timeout covers the check through its JSON body. DownloadAsync has
            // a separate five-minute bound because ResponseHeadersRead leaves body reads to
            // its cancellation token.
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateService.UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var updates = new UpdateService(http);

            using var checkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var info = updates.CheckForUpdateAsync(checkTimeout.Token).GetAwaiter().GetResult();
            if (info is null) return false;

            using var progress = new UpdateProgressForm(info.TagName);
            progress.Show();

            // No message loop is running yet (Application.Run is further down in Run()), so
            // the download is pumped by hand to keep the window responsive and repainting.
            var applyUpdate = TryDownloadAndApplyStartupUpdateAsync(
                updates,
                info,
                path =>
                {
                    progress.SetStatus("Restarting…");
                    Application.DoEvents();
                    UpdateService.InstallAndRestart(path);
                },
                new Progress<double>(progress.SetProgress));
            while (!applyUpdate.IsCompleted)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
            return applyUpdate.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Update check skipped: {ex.Message}");
            return false;
        }
    }

    internal static async Task<bool> TryDownloadAndApplyStartupUpdateAsync(
        UpdateService updates,
        UpdateInfo info,
        Action<string> install,
        IProgress<double>? progress = null)
    {
        try
        {
            var path = await updates.DownloadAsync(info, progress);
            install(path);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Startup update skipped: {ex.Message}");
            return false;
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
