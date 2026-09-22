using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace InventoryTracker.App;

/// <summary>A newer release than the one currently running.</summary>
public sealed record UpdateInfo(Version Version, string TagName, string HtmlUrl, string DownloadUrl, long? SizeBytes);

/// <summary>
/// Checks GitHub Releases for a newer build and, when one is found, downloads it and swaps
/// it in for the running executable.
/// <para>
/// The exe is published as a single self-contained file (see publish.ps1), so there is
/// nothing to "install" beyond replacing that one file. The running process cannot
/// overwrite or delete its own locked exe, so <see cref="InstallAndRestart"/> hands the
/// swap to a detached helper that waits for this process to exit first.
/// </para>
/// </summary>
public sealed class UpdateService(HttpClient http)
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/PetitCastor/inventory-tracker/releases/latest";

    /// <summary>Outbound identification for both the GitHub API and the download itself.</summary>
    public const string UserAgent = "InventoryTracker-Updater/1.0";

    /// <summary>
    /// The version of the running build, read from the assembly version publish.ps1 stamps
    /// from VERSION. Local dev builds (no -p:Version) get the compiler's own default of
    /// 1.0.0.0, which sorts above every real release, so an unstamped build never offers to
    /// "update" itself. The null-coalesced 0.0.0 below is only a defensive fallback for the
    /// (practically unreachable) case where a loaded assembly reports no version at all —
    /// it is not what an unstamped build actually gets.
    /// </summary>
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Asks GitHub for the latest release and returns it if its version is newer than
    /// <see cref="CurrentVersion"/> and it carries a usable .exe asset. Null otherwise —
    /// including on any network or parse failure, which callers should treat as "no update
    /// available right now" rather than an error to surface.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var doc = await http.GetFromJsonAsync<JsonDocument>(LatestReleaseUrl, ct);
        if (doc is null) return null;

        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagProp) || tagProp.GetString() is not { } tag)
        {
            return null;
        }

        var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version <= CurrentVersion)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null) continue;

            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64()
                : (long?)null;

            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new UpdateInfo(version, tag, htmlUrl, url, size);
        }

        return null;
    }

    /// <summary>
    /// Downloads the release exe to a temp file and returns its path. Reports 0-100 as bytes
    /// arrive when the response carries a length; otherwise progress is never reported.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "InventoryTrackerUpdate");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"InventoryTracker-{info.TagName}.exe");

        using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? info.SizeBytes;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = File.Create(target))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (total is > 0) progress?.Report(100.0 * readTotal / total.Value);
            }
        }

        return target;
    }

    /// <summary>
    /// Hands off to a detached PowerShell helper that waits for this process to exit, then
    /// moves the downloaded exe over the running one and starts it. Call this last: the
    /// process should exit (return from Run/Main) immediately after, without doing anything
    /// else that assumes the current exe still exists.
    /// <para>
    /// The swap renames the running exe aside rather than deleting it up front, and only
    /// deletes that backup once the new exe is confirmed in place and started — a failed
    /// <c>Move-Item</c> (partial download, AV quarantine, permissions) then rolls back to the
    /// backup instead of leaving the app deleted with nothing to relaunch.
    /// </para>
    /// </summary>
    public static void InstallAndRestart(string downloadedExePath)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable's path.");
        var pid = Environment.ProcessId;

        var scriptPath = Path.Combine(Path.GetTempPath(), $"InventoryTracker-update-{pid}.ps1");
        var backupExe = currentExe + ".old";
        var backupName = Path.GetFileName(backupExe);
        var currentName = Path.GetFileName(currentExe);

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try { Wait-Process -Id {{pid}} -Timeout 30 -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Milliseconds 500

            Remove-Item -LiteralPath {{Quote(backupExe)}} -Force -ErrorAction SilentlyContinue

            try {
                Rename-Item -LiteralPath {{Quote(currentExe)}} -NewName {{Quote(backupName)}} -Force
                Move-Item -LiteralPath {{Quote(downloadedExePath)}} -Destination {{Quote(currentExe)}} -Force
                Start-Process -FilePath {{Quote(currentExe)}}
                Remove-Item -LiteralPath {{Quote(backupExe)}} -Force -ErrorAction SilentlyContinue
            }
            catch {
                if ((Test-Path -LiteralPath {{Quote(backupExe)}}) -and -not (Test-Path -LiteralPath {{Quote(currentExe)}})) {
                    Rename-Item -LiteralPath {{Quote(backupExe)}} -NewName {{Quote(currentName)}} -Force
                }
                if (Test-Path -LiteralPath {{Quote(currentExe)}}) {
                    Start-Process -FilePath {{Quote(currentExe)}}
                }
            }

            Remove-Item -LiteralPath {{Quote(scriptPath)}} -Force -ErrorAction SilentlyContinue
            """;
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>Single-quotes a value for embedding in the generated PowerShell script,
    /// doubling any embedded single quote the way PowerShell itself expects.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
