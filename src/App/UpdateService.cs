using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace InventoryTracker.App;

/// <summary>A newer release than the one currently running.</summary>
public sealed record UpdateInfo(Version Version, string TagName, string HtmlUrl, string DownloadUrl, long? SizeBytes);

internal enum UpdateInstallResult
{
    Confirmed,
    RolledBack,
    Failed,
}

internal sealed record UpdateInstallTiming(
    TimeSpan ConfirmationTimeout,
    TimeSpan StabilityWindow,
    TimeSpan? ParentExitTimeout = null);

internal sealed record UpdateInstallAcknowledgement(string Nonce, int ProcessId);
internal sealed record UpdateInstallProcess(int ProcessId, long StartedAt);

/// <summary>Operations used by the update replacement transaction and its deterministic tests.</summary>
internal interface IUpdateInstallOperations
{
    Task<bool> WaitForParentExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);
    long GetTimestamp();
    TimeSpan GetElapsedTime(long timestamp);
    bool FileExists(string path);
    void MoveFile(string source, string destination);
    void DeleteFile(string path);
    UpdateInstallProcess StartCandidate(string path, string pipeName, string nonce);
    Task<UpdateInstallAcknowledgement?> WaitForAcknowledgementAsync(
        string pipeName, TimeSpan timeout, CancellationToken cancellationToken);
    bool HasExited(int processId);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    void StopCandidate(int processId);
    void StartRollback(string path, string argument);
    void ScheduleHelperCleanupAfterExit(string path, int processId);
    void ReportFailure(Exception exception, string? recoveryPath = null, bool rollbackFailed = false);
}

/// <summary>
/// Injectable replacement state machine shared by the update helper and deterministic tests.
/// </summary>
internal static class UpdateInstallTransaction
{
    internal static Task<UpdateInstallResult> ExecuteAsync(
        string currentExePath,
        string downloadedExePath,
        string backupExePath,
        string helperCopyPath,
        int parentProcessId,
        int helperProcessId,
        IUpdateInstallOperations operations,
        UpdateInstallTiming timing,
        CancellationToken cancellationToken = default) => ExecuteCoreAsync(
            currentExePath, downloadedExePath, backupExePath, helperCopyPath, parentProcessId,
            helperProcessId, operations, timing, cancellationToken);

    private static async Task<UpdateInstallResult> ExecuteCoreAsync(
        string currentExePath,
        string downloadedExePath,
        string backupExePath,
        string helperCopyPath,
        int parentProcessId,
        int helperProcessId,
        IUpdateInstallOperations operations,
        UpdateInstallTiming timing,
        CancellationToken cancellationToken)
    {
        var backupCreated = false;
        int? candidateProcessId = null;
        var result = UpdateInstallResult.Failed;
        long candidateStartedAt = 0;

        try
        {
            if (!await operations.WaitForParentExitAsync(
                    parentProcessId, timing.ParentExitTimeout ?? TimeSpan.FromSeconds(30), cancellationToken))
            {
                operations.ReportFailure(new TimeoutException("The original application did not exit before the helper deadline."));
                return result;
            }

            operations.MoveFile(currentExePath, backupExePath);
            backupCreated = true;
            operations.MoveFile(downloadedExePath, currentExePath);

            var pipeName = $"InventoryTracker.Update.{Guid.NewGuid():N}";
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var candidate = operations.StartCandidate(currentExePath, pipeName, nonce);
            candidateProcessId = candidate.ProcessId;
            candidateStartedAt = candidate.StartedAt;

            var acknowledgementTimeout = timing.ConfirmationTimeout - operations.GetElapsedTime(candidateStartedAt);
            if (acknowledgementTimeout <= TimeSpan.Zero)
            {
                operations.ReportFailure(new TimeoutException("The update confirmation deadline expired at candidate launch."));
                result = RollBack();
                return result;
            }

            var acknowledgement = await operations.WaitForAcknowledgementAsync(
                pipeName, acknowledgementTimeout, cancellationToken);
            if (acknowledgement is null || acknowledgement.Nonce != nonce ||
                acknowledgement.ProcessId != candidateProcessId.Value || operations.HasExited(candidateProcessId.Value) ||
                operations.GetElapsedTime(candidateStartedAt) > timing.ConfirmationTimeout)
            {
                operations.ReportFailure(new InvalidOperationException("The update candidate did not provide a valid startup confirmation."));
                result = RollBack();
                return result;
            }

            var remaining = timing.ConfirmationTimeout - operations.GetElapsedTime(candidateStartedAt);
            if (remaining < timing.StabilityWindow)
            {
                operations.ReportFailure(new TimeoutException("The candidate confirmation arrived too late to complete the stability window."));
                result = RollBack();
                return result;
            }

            await operations.DelayAsync(timing.StabilityWindow, cancellationToken);
            if (operations.HasExited(candidateProcessId.Value) ||
                operations.GetElapsedTime(candidateStartedAt) > timing.ConfirmationTimeout)
            {
                operations.ReportFailure(new InvalidOperationException("The update candidate exited or exceeded its confirmation deadline during the stability window."));
                result = RollBack();
                return result;
            }

            operations.DeleteFile(backupExePath);
            backupCreated = false;
            result = UpdateInstallResult.Confirmed;
            return result;
        }
        catch (Exception exception)
        {
            operations.ReportFailure(exception);
            result = RollBack();
            return result;
        }
        finally
        {
            try { operations.ScheduleHelperCleanupAfterExit(helperCopyPath, helperProcessId); }
            catch (Exception exception) { operations.ReportFailure(exception); }
        }

        UpdateInstallResult RollBack()
        {
            try
            {
                if (candidateProcessId is { } pid && !operations.HasExited(pid))
                {
                    operations.StopCandidate(pid);
                }

                if (backupCreated)
                {
                    if (operations.FileExists(currentExePath)) operations.DeleteFile(currentExePath);
                    operations.MoveFile(backupExePath, currentExePath);
                    backupCreated = false;
                }

                if (operations.FileExists(currentExePath))
                {
                    operations.StartRollback(currentExePath, "--skip-update-once");
                    return UpdateInstallResult.RolledBack;
                }
            }
            catch (Exception exception)
            {
                string? recoveryPath = null;
                try
                {
                    if (operations.FileExists(backupExePath)) recoveryPath = backupExePath;
                    else if (operations.FileExists(currentExePath)) recoveryPath = currentExePath;
                }
                catch (Exception inspectionException) { operations.ReportFailure(inspectionException); }
                operations.ReportFailure(exception, recoveryPath, rollbackFailed: true);
                // Keep the backup intact on rollback failure so the last recoverable copy remains.
            }

            return UpdateInstallResult.Failed;
        }
    }
}

/// <summary>
/// Checks GitHub Releases for a newer build and, when one is found, downloads it and swaps
/// it in for the running executable.
/// <para>
/// The exe is published as a single self-contained file (see publish.ps1), so there is
/// nothing to "install" beyond replacing that one file. The running process cannot
/// overwrite or delete its own locked exe, so <see cref="InstallAndRestart"/> starts a
/// unique temporary copy that waits for this process to exit before running the transaction.
/// </para>
/// </summary>
public sealed class UpdateService(HttpClient http)
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/PetitCastor/inventory-tracker/releases/latest";

    /// <summary>Outbound identification for both the GitHub API and the download itself.</summary>
    public const string UserAgent = "InventoryTracker-Updater/1.0";

    private readonly TimeSpan _downloadTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    internal UpdateService(HttpClient http, TimeSpan downloadTimeout, TimeProvider timeProvider) : this(http)
    {
        _downloadTimeout = downloadTimeout;
        _timeProvider = timeProvider;
    }

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
            if (!string.Equals(name, "InventoryTracker.exe", StringComparison.Ordinal)) continue;

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
        if (info.SizeBytes is not > 0)
        {
            throw new InvalidDataException("The release asset does not include a positive expected size.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "InventoryTrackerUpdate");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"InventoryTracker-{info.TagName}-{Guid.NewGuid():N}.exe");
        var targetCreated = false;
        using var timeout = new CancellationTokenSource(_downloadTimeout, _timeProvider);
        using var download = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var downloadToken = download.Token;

        try
        {
            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadToken);
            response.EnsureSuccessStatusCode();

            var responseLength = response.Content.Headers.ContentLength;
            if (responseLength is not null && responseLength.Value != info.SizeBytes.Value)
            {
                throw new InvalidDataException("The release response length does not match the expected asset size.");
            }

            var total = info.SizeBytes.Value;

            await using (var source = await response.Content.ReadAsStreamAsync(downloadToken))
            await using (var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                targetCreated = true;
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, downloadToken)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), downloadToken);
                    readTotal += read;
                    progress?.Report(100.0 * readTotal / total);
                }
            }

            downloadToken.ThrowIfCancellationRequested();
            if (new FileInfo(target).Length != info.SizeBytes.Value)
            {
                throw new InvalidDataException("The downloaded release asset is incomplete.");
            }

            return target;
        }
        catch
        {
            if (targetCreated) File.Delete(target);
            throw;
        }
    }

    /// <summary>
    /// Hands off to a detached copy of this executable that waits for this process to exit,
    /// then replaces the app and confirms the candidate startup. Call this last: the
    /// process should exit (return from Run/Main) immediately after, without doing anything
    /// else that assumes the current exe still exists.
    /// <para>
    /// The swap preserves the old exe under a unique backup name until the candidate has
    /// acknowledged readiness and stayed alive through the stability window.
    /// </para>
    /// </summary>
    public static void InstallAndRestart(string downloadedExePath)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable's path.");
        var helperCopy = Path.Combine(Path.GetTempPath(), $"InventoryTracker-update-{Guid.NewGuid():N}.exe");
        File.Copy(currentExe, helperCopy);

        var start = new ProcessStartInfo(helperCopy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--update-helper");
        start.ArgumentList.Add(currentExe);
        start.ArgumentList.Add(downloadedExePath);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            if (Process.Start(start) is null)
            {
                throw new InvalidOperationException("Could not start the update helper.");
            }
        }
        catch
        {
            File.Delete(helperCopy);
            throw;
        }
    }

    internal static int RunInstallHelper(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], out var parentProcessId)) return 2;

        var helperPath = Environment.ProcessPath;
        if (helperPath is null) return 2;

        var currentExe = args[0];
        var downloadedExe = args[1];
        var backupExe = Path.Combine(Path.GetDirectoryName(currentExe)!,
            $"{Path.GetFileName(currentExe)}.{Guid.NewGuid():N}.old");
        var operations = new ProcessUpdateInstallOperations();
        return UpdateInstallTransaction.ExecuteAsync(
                currentExe, downloadedExe, backupExe, helperPath, parentProcessId, Environment.ProcessId,
                operations, new UpdateInstallTiming(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)))
            .GetAwaiter().GetResult() == UpdateInstallResult.Confirmed ? 0 : 1;
    }

    internal static void SendStartupConfirmation(string pipeName, string nonce)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        client.Connect(30000);
        using var writer = new StreamWriter(client, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
        writer.WriteLine($"{nonce}|{Environment.ProcessId}");
        writer.Flush();
    }
}

internal sealed class ProcessUpdateInstallOperations : IUpdateInstallOperations
{
    private readonly Dictionary<int, Process> _candidates = [];

    public async Task<bool> WaitForParentExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; }

        using (parent)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(timeout);
            try
            {
                await parent.WaitForExitAsync(deadline.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    public bool FileExists(string path) => File.Exists(path);
    public void MoveFile(string source, string destination) => File.Move(source, destination);
    public void DeleteFile(string path) => File.Delete(path);
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

    public UpdateInstallProcess StartCandidate(string path, string pipeName, string nonce)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--update-confirm");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(nonce);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch update candidate.");
        _candidates.Add(process.Id, process);
        return new UpdateInstallProcess(process.Id, Stopwatch.GetTimestamp());
    }

    public async Task<UpdateInstallAcknowledgement?> WaitForAcknowledgementAsync(
        string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var message = await reader.ReadLineAsync(deadline.Token);
            if (message is null) return null;
            var parts = message.Split('|', StringSplitOptions.None);
            return parts.Length == 2 && int.TryParse(parts[1], out var processId)
                ? new UpdateInstallAcknowledgement(parts[0], processId)
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public bool HasExited(int processId) => !_candidates.TryGetValue(processId, out var process) || process.HasExited;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    public void StopCandidate(int processId)
    {
        if (!_candidates.TryGetValue(processId, out var process) || process.HasExited) return;
        process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(5000))
        {
            throw new TimeoutException($"Update candidate process {processId} did not close after termination.");
        }
    }

    public void StartRollback(string path, string argument)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(argument);
        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("Could not restart the previous version.");
        }
    }

    public void ScheduleHelperCleanupAfterExit(string path, int processId)
    {
        var literalPath = path.Replace("'", "''", StringComparison.Ordinal);
        var command = $"while (Get-Process -Id {processId} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 200 }}; Remove-Item -LiteralPath '{literalPath}' -Force -ErrorAction SilentlyContinue";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("Could not start helper cleanup.");
        }
    }

    public void ReportFailure(Exception exception, string? recoveryPath = null, bool rollbackFailed = false)
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"InventoryTracker-update-{Environment.ProcessId}.log");
        var message = $"{DateTimeOffset.Now:O} Update helper failure: {exception}; recoveryPath={recoveryPath ?? "(none)"}{Environment.NewLine}";
        Trace.WriteLine(message);
        try { File.AppendAllText(logPath, message); }
        catch (Exception logException) { Trace.WriteLine($"Could not write update diagnostic log '{logPath}': {logException}"); }

        if (rollbackFailed)
        {
            System.Windows.Forms.MessageBox.Show(
                recoveryPath is null
                    ? $"The update failed and automatic rollback could not finish. No recoverable executable path was found. Diagnostic log: {logPath}"
                    : $"The update failed and automatic rollback could not finish. The previous application is preserved at:\n\n{recoveryPath}\n\nKeep this file to restore the previous version. Diagnostic log: {logPath}",
                "Inventory Tracker update failed",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }
}
