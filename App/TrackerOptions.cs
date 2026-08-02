using LogParser.Data;
using LogParser.Ingest;

namespace LogParser.App;

/// <summary>Where the tray app reads from and writes to, and which port the UI listens on.</summary>
public sealed class TrackerOptions
{
    public string LogDir { get; set; } = LogFileLocator.DefaultLogDir;
    public string DatabasePath { get; set; } = TrackerDb.DefaultPath;

    /// <summary>Fixed so the bookmarked URL keeps working across restarts.</summary>
    public int Port { get; set; } = 5730;

    public string Url => $"http://localhost:{Port}";

    /// <summary>
    /// Reads the same switches the CLI accepts, so a non-default install path or an
    /// alternate store can be pointed at without editing anything.
    /// </summary>
    public static TrackerOptions FromArgs(string[] args)
    {
        var options = new TrackerOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log-dir" when i + 1 < args.Length:
                    options.LogDir = args[++i];
                    break;
                case "--db" when i + 1 < args.Length:
                    options.DatabasePath = args[++i];
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    options.Port = port;
                    i++;
                    break;
            }
        }

        return options;
    }
}
