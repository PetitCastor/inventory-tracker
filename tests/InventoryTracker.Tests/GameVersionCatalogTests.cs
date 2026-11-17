using InventoryTracker.Ingest;

namespace InventoryTracker.Tests;

public sealed class GameVersionCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-launcher-{Guid.NewGuid():N}");

    public GameVersionCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines) + "\n");

    private static string Installing(string at, string tag) =>
        $"{{ \"t\":\"{at}\", \"[main][info] \": \"[Pipeline] Installing Star Citizen LIVE {tag} at E:\\\\Games\\\\StarCitizen (type: update, forceDP: false)\"  }},";

    [Fact]
    public void Names_a_build_by_the_version_the_launcher_installed()
    {
        Write("log.log", Installing("2026-11-12 13:35:43.609", "4.10.3-live.13165946"));

        Assert.Equal("4.10.3", GameVersionCatalog.VersionOf(13165946, _dir));
        Assert.Equal("Star Citizen 4.10.3 (build 13165946)", GameVersionCatalog.Describe(13165946, _dir));
    }

    [Fact]
    public void Reads_the_rotated_log_too()
    {
        Write("log.old.log", Installing("2026-08-26 17:08:18.280", "4.10.0-live.12519617"));
        Write("log.log", Installing("2026-11-12 13:35:43.609", "4.10.3-live.13165946"));

        Assert.Equal("4.10.0", GameVersionCatalog.VersionOf(12519617, _dir));
        Assert.Equal("4.10.3", GameVersionCatalog.VersionOf(13165946, _dir));
    }

    [Fact]
    public void Falls_back_to_the_build_when_the_launcher_never_named_it()
    {
        Write("log.log", Installing("2026-11-12 13:35:43.609", "4.10.3-live.13165946"));

        Assert.Null(GameVersionCatalog.VersionOf(12913470, _dir));
        Assert.Equal("Star Citizen build 12913470", GameVersionCatalog.Describe(12913470, _dir));
    }

    [Fact]
    public void Has_nothing_to_say_without_launcher_logs()
    {
        Assert.Empty(GameVersionCatalog.Load(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Sees_a_build_installed_after_it_last_read()
    {
        Write("log.log", Installing("2026-10-13 18:31:11.283", "4.10.2-live.12913470"));
        Assert.Null(GameVersionCatalog.VersionOf(13165946, _dir));

        File.AppendAllText(Path.Combine(_dir, "log.log"), Installing("2026-11-12 13:35:43.609", "4.10.3-live.13165946") + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "log.log"), DateTime.UtcNow.AddMinutes(1));

        Assert.Equal("4.10.3", GameVersionCatalog.VersionOf(13165946, _dir));
    }
}
