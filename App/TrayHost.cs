using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using LogParser.Components;
using LogParser.Data;
using LogParser.Ingest;
using LogParser.Naming;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogParser.App;

/// <summary>
/// Runs the tracker as a tray application: a Blazor Server UI on loopback plus a
/// background service following Game.log, with a notify icon to reach both.
/// </summary>
public static class TrayHost
{
    private const string WikiClient = "star-citizen-wiki";

    public static int Run(string[] args)
    {
        var options = TrackerOptions.FromArgs(args);

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(options.Url);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var db = new TrackerDb(options.DatabasePath);
        db.Initialize();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<TrackerState>();

        // Registered by hand rather than as a typed client: the service caches the whole
        // catalogue in memory, so it has to be a singleton, and AddHttpClient<T> would
        // register it transient and quietly win over an AddSingleton for the same type.
        builder.Services.AddHttpClient(WikiClient, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SCLogParser/1.0 (inventory tracker)");
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

        app.MapRazorComponents<Root>().AddInteractiveServerRenderMode();

        // Kestrel runs alongside the WinForms message loop rather than owning the thread.
        app.Start();

        using var tray = BuildTrayIcon(app, options);
        Application.Run();

        app.StopAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static NotifyIcon BuildTrayIcon(WebApplication app, TrackerOptions options)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Open inventory finder", null, (_, _) => OpenBrowser(options.Url));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Rescan logs now", null, (_, _) =>
            app.Services.GetRequiredService<LogWatchService>().RequestScan());

        menu.Items.Add("Refresh item names from wiki", null, async (_, _) =>
        {
            var names = app.Services.GetRequiredService<WikiNameService>();
            await names.RefreshAsync();
            app.Services.GetRequiredService<TrackerState>().Rebuild();
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Star Citizen inventory finder",
            Visible = true,
            ContextMenuStrip = menu,
        };

        icon.DoubleClick += (_, _) => OpenBrowser(options.Url);
        return icon;
    }

    private static void OpenBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
