using System.Text;
using InventoryTracker.Ingest;

namespace InventoryTracker.Tests;

public sealed class LogFileLocatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-{Guid.NewGuid():N}");

    public LogFileLocatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void DefaultLogDir_is_probed_without_throwing()
    {
        // Regression: DefaultLogDir was declared above the arrays its probe reads, and
        // static field initialisers run in declaration order — so the probe read nulls and
        // the type initializer threw on first touch, taking the whole app down at startup.
        var probed = LogFileLocator.DefaultLogDir;

        Assert.NotNull(probed);
    }

    [Fact]
    public void DefaultLogDir_prefers_a_LIVE_install_over_a_PTU_one()
    {
        // Players routinely have both, on different drives. Probing drive-first hands back
        // whichever drive sorts earlier, so a D:\...\PTU install can win over the E:\...\LIVE
        // the player actually plays — which is what happened before channel became the
        // outermost loop.
        var probed = LogFileLocator.DefaultLogDir;

        if (probed.Length == 0) return; // no install on this machine; nothing to assert

        if (probed.EndsWith("PTU", StringComparison.OrdinalIgnoreCase) ||
            probed.EndsWith("EPTU", StringComparison.OrdinalIgnoreCase))
        {
            // A non-LIVE result is only correct when no LIVE install exists anywhere.
            var liveSiblings = Directory
                .GetParent(probed)?
                .GetDirectories("LIVE");

            Assert.True(
                liveSiblings is null or { Length: 0 },
                $"probed {probed} even though a LIVE install sits beside it");
        }
    }

    [Fact]
    public void Discover_finds_the_live_log_and_marks_it_live()
    {
        File.WriteAllText(Path.Combine(_dir, "Game.log"), "<2026-07-16T10:00:00.000Z> [Notice] <X> y\n");

        var found = LogFileLocator.Discover(_dir, new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));

        var live = Assert.Single(found);
        Assert.True(live.IsLive);
    }

    [Fact]
    public void Discover_admits_a_backup_by_its_build_id_not_its_timestamp()
    {
        // Selection is by build id so a backup touched by a restore, sync or antivirus pass
        // is not admitted, and a genuine 4.9 file with an old mtime is not dropped.
        var backups = Path.Combine(_dir, "logbackups");
        Directory.CreateDirectory(backups);

        var recent = Path.Combine(backups, "Game Build(12232306) 31 Jul 26 (21 28 10).log");
        var old = Path.Combine(backups, "Game Build(12000000) 31 Jul 26 (21 28 10).log");
        File.WriteAllText(recent, "x");
        File.WriteAllText(old, "x");

        // Both files are new on disk; only the build id separates them.
        var found = LogFileLocator.Discover(_dir, DateTimeOffset.UtcNow.AddYears(-1));

        Assert.Contains(found, f => f.Path == recent);
        Assert.DoesNotContain(found, f => f.Path == old);
    }

    [Fact]
    public void FirstTimestamp_reads_the_first_parseable_line()
    {
        var path = Path.Combine(_dir, "Game.log");
        File.WriteAllText(
            path,
            "a header line that does not parse\n" +
            "<2026-07-16T11:20:52.355Z> [Notice] <X> first real record\n" +
            "<2026-07-16T11:20:53.000Z> [Notice] <X> second\n",
            new UTF8Encoding(false));

        Assert.Equal(
            new DateTimeOffset(2026, 7, 16, 11, 20, 52, 355, TimeSpan.Zero),
            LogFileLocator.FirstTimestamp(path));
    }

    [Fact]
    public void FirstTimestamp_is_null_when_nothing_near_the_head_parses()
    {
        var path = Path.Combine(_dir, "Game.log");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("noise\n", 500)));

        Assert.Null(LogFileLocator.FirstTimestamp(path));
    }

    [Fact]
    public void FirstTimestamp_is_null_for_a_missing_file()
    {
        Assert.Null(LogFileLocator.FirstTimestamp(Path.Combine(_dir, "does-not-exist.log")));
    }

    [Fact]
    public void FirstTimestamp_distinguishes_a_rotated_log_from_the_one_before_it()
    {
        // This is what rotation detection rests on: the path is unchanged across a rotation,
        // so the byte watermark alone cannot tell a longer file from a different one.
        var before = Path.Combine(_dir, "before.log");
        var after = Path.Combine(_dir, "after.log");

        File.WriteAllText(before, "<2026-07-16T10:00:00.000Z> [Notice] <X> y\n");
        File.WriteAllText(after, "<2026-07-17T09:00:00.000Z> [Notice] <X> y\n");

        Assert.NotEqual(LogFileLocator.FirstTimestamp(before), LogFileLocator.FirstTimestamp(after));
    }
}
