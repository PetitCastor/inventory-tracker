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

    /// <summary>The port the local UI listens on. Fixed so a bookmark keeps working.</summary>
    public const int DefaultPort = 5730;

    /// <summary>Same directory as the database: both are this install's private state.</summary>
    public static string DefaultPath => Path.Combine(
        Path.GetDirectoryName(TrackerDb.DefaultPath) ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InventoryTracker"),
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
        var existed = File.Exists(path);
        var config = existed ? ReadOrDefault(path) : new AppConfig();

        var changed = !existed;
        if (config.LogDir is null) { config.LogDir = LogFileLocator.DefaultLogDir; changed = true; }
        if (config.DatabasePath is null) { config.DatabasePath = TrackerDb.DefaultPath; changed = true; }
        if (config.Port is null) { config.Port = DefaultPort; changed = true; }

        if (changed) config.Save(path);
        return config;
    }

    /// <summary>
    /// Reads the config, or returns a blank one after moving an unreadable file aside.
    /// <para>
    /// The file is never overwritten in place on a parse failure. Load fills the blank
    /// config's defaults and saves it, so overwriting here would destroy InceptionDate and
    /// SetupComplete — sending the user back through the setup wizard with no way to
    /// recover what was lost. Keeping the original under .corrupt makes that repairable,
    /// and the tray app has no console for a message to go to.
    /// </para>
    /// </summary>
    private static AppConfig ReadOrDefault(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"Could not read config at {path}, using defaults: {ex.Message}");
            TryPreserveCorrupt(path);
            return new AppConfig();
        }
    }

    private static void TryPreserveCorrupt(string path)
    {
        try
        {
            var kept = path + ".corrupt";
            File.Delete(kept);
            File.Move(path, kept);
            Console.Error.WriteLine($"Original kept at {kept}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. Losing the copy is bad; failing to start over it would be worse.
        }
    }

    /// <summary>
    /// Writes the config atomically. A direct WriteAllText truncates before it writes, so a
    /// crash or power cut mid-write leaves a half-written file that fails to parse on the
    /// next launch — the exact input <see cref="ReadOrDefault"/> has to recover from.
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        var temp = path + ".tmp";

        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
