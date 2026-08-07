using System.Text.Json;
using System.Text.Json.Serialization;
using InventoryTracker.Data;
using InventoryTracker.Ingest;

namespace InventoryTracker.App;

/// <summary>
/// The single source of settings for both the CLI and the tray app. There are no
/// command-line overrides for any of this — edit config.json and relaunch.
/// </summary>
public sealed class AppConfig
{
    public string? LogDir { get; set; }
    public string? DatabasePath { get; set; }
    public int? Port { get; set; }

    /// <summary>Everything logged before this is ignored. Null means no inception date is set.</summary>
    public DateTimeOffset? InceptionDate { get; set; }

    /// <summary>
    /// False until the first-run setup wizard has been completed. A brand-new config.json
    /// leaves this false, which is exactly the signal the UI uses to gate the app behind
    /// <c>/setup</c> until the user has confirmed a log directory and inception date.
    /// </summary>
    public bool SetupComplete { get; set; }

    /// <summary>Same directory as the database: both are this install's private state.</summary>
    public static string DefaultPath => Path.Combine(
        Path.GetDirectoryName(TrackerDb.DefaultPath) ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCLogParser"),
        "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reads config.json, filling and persisting any unset field with its default so the
    /// file exists and is fully populated after the very first run — there is nowhere
    /// else left to look these values up.
    /// </summary>
    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        var config = File.Exists(path) ? ReadOrDefault(path) : new AppConfig();

        var changed = !File.Exists(path);
        if (config.LogDir is null) { config.LogDir = LogFileLocator.DefaultLogDir; changed = true; }
        if (config.DatabasePath is null) { config.DatabasePath = TrackerDb.DefaultPath; changed = true; }
        if (config.Port is null) { config.Port = 5730; changed = true; }

        if (changed) config.Save(path);
        return config;
    }

    private static AppConfig ReadOrDefault(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"Could not read config at {path}, using defaults: {ex.Message}");
            return new AppConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
