using InventoryTracker.App;
using Microsoft.AspNetCore.Builder;

namespace InventoryTracker.Tests;

public sealed class UpdateInstallTests
{
    [Fact]
    public void TryParseStartupArguments_UpdateConfirmationKeepsUpdaterValuesOutOfApplicationArguments()
    {
        var pipeName = $"InventoryTracker.Update.{Guid.NewGuid():N}";
        const string nonce = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        var parsed = TrayHost.TryParseStartupArguments(
            ["--update-confirm", pipeName, nonce], out var startupArguments);

        Assert.True(parsed);
        Assert.True(startupArguments.IsUpdateConfirmation);
        Assert.Equal(pipeName, startupArguments.ConfirmationPipeName);
        Assert.Equal(nonce, startupArguments.ConfirmationNonce);
        Assert.Empty(startupArguments.ApplicationArguments);
        Assert.False(startupArguments.SkipUpdateOnce);
        Assert.NotNull(WebApplication.CreateBuilder(startupArguments.ApplicationArguments));
    }

    [Fact]
    public void TryParseStartupArguments_RejectsMalformedUpdateConfirmationArguments()
    {
        var parsed = TrayHost.TryParseStartupArguments(
            ["--update-confirm", "not-a-private-pipe", "not-a-nonce"], out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TryParseStartupArguments_SkipUpdateOnceKeepsUpdaterSwitchOutOfApplicationArguments()
    {
        var parsed = TrayHost.TryParseStartupArguments(["--skip-update-once"], out var startupArguments);

        Assert.True(parsed);
        Assert.True(startupArguments.SkipUpdateOnce);
        Assert.Empty(startupArguments.ApplicationArguments);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOldToBackupMoveFails_KeepsAndRelaunchesOldFile()
    {
        using var fixture = new Fixture();
        fixture.Operations.FailMove = (source, _) => source == fixture.Current;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
        Assert.False(File.Exists(fixture.Backup));
        Assert.Contains((fixture.Current, "--skip-update-once"), fixture.Operations.Restarts);
        Assert.DoesNotContain(fixture.Current, fixture.Operations.Deleted);
    }

    [Fact]
    public async Task ExecuteAsync_WhenParentDoesNotExit_LeavesInstallationUntouched()
    {
        using var fixture = new Fixture();
        fixture.Operations.ParentExited = false;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.Failed, result);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
        Assert.True(File.Exists(fixture.Download));
        Assert.False(File.Exists(fixture.Backup));
        Assert.Empty(fixture.Operations.Restarts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDownloadToTargetMoveFails_RestoresBackupAndRelaunchesOldFile()
    {
        using var fixture = new Fixture();
        fixture.Operations.FailMove = (source, destination) => source == fixture.Download && destination == fixture.Current;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
        Assert.False(File.Exists(fixture.Backup));
        Assert.Contains((fixture.Current, "--skip-update-once"), fixture.Operations.Restarts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCandidateLaunchFails_RemovesCandidateAndRestoresBackup()
    {
        using var fixture = new Fixture();
        fixture.Operations.FailCandidateLaunch = true;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
        Assert.False(File.Exists(fixture.Backup));
        Assert.Contains(fixture.Current, fixture.Operations.Deleted);
        Assert.Contains((fixture.Current, "--skip-update-once"), fixture.Operations.Restarts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCandidateExitsImmediately_RestoresBackup()
    {
        using var fixture = new Fixture();
        fixture.Operations.CandidateExited = true;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
        Assert.False(File.Exists(fixture.Backup));
    }

    [Theory]
    [InlineData("wrong-nonce", 42)]
    [InlineData(null, 7)]
    public async Task ExecuteAsync_WhenAcknowledgementNonceOrPidIsWrong_RestoresBackup(string? nonce, int pid)
    {
        using var fixture = new Fixture();
        fixture.Operations.AcknowledgementNonceOverride = nonce;
        fixture.Operations.AcknowledgementProcessIdOverride = pid;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
        Assert.False(File.Exists(fixture.Backup));
    }

    [Fact]
    public async Task ExecuteAsync_WhenAcknowledgementIsMissing_RestoresBackupWithinConfirmationTimeout()
    {
        using var fixture = new Fixture();
        fixture.Operations.MissingAcknowledgement = true;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Operations.ConfirmationTimeout);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
    }

    [Fact]
    public async Task ExecuteAsync_WhenConfirmationTimesOut_RestoresBackup()
    {
        using var fixture = new Fixture();
        fixture.Operations.MissingAcknowledgement = true;
        fixture.Operations.ConfirmationTimedOut = true;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Operations.ConfirmationTimeout);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
    }

    [Fact]
    public async Task ExecuteAsync_StabilityWindowMustFitInsideThirtySecondLaunchDeadline()
    {
        using var fixture = new Fixture();
        fixture.Operations.AcknowledgementElapsed = TimeSpan.FromSeconds(29.5);

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Operations.ConfirmationTimeout);
        Assert.Equal(TimeSpan.Zero, fixture.Operations.StabilityDelay);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
    }

    [Fact]
    public async Task ExecuteAsync_WhenRollbackCannotRestoreBackup_KeepsBackupAndReportsItsPath()
    {
        using var fixture = new Fixture();
        fixture.Operations.AcknowledgementNonceOverride = "wrong-nonce";
        fixture.Operations.FailMove = (source, destination) => source == fixture.Backup && destination == fixture.Current;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.Failed, result);
        Assert.False(File.Exists(fixture.Current));
        Assert.True(File.Exists(fixture.Backup));
        Assert.Contains(fixture.Operations.Failures,
            failure => failure.RollbackFailed && failure.RecoveryPath == fixture.Backup);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRestoredExecutableCannotBeLaunched_ReportsItsPreservedPath()
    {
        using var fixture = new Fixture();
        fixture.Operations.AcknowledgementNonceOverride = "wrong-nonce";
        fixture.Operations.FailRollbackLaunch = true;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.Failed, result);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
        Assert.False(File.Exists(fixture.Backup));
        Assert.Contains(fixture.Operations.Failures,
            failure => failure.RollbackFailed && failure.RecoveryPath == fixture.Current);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAcknowledgedCandidateStaysHealthy_DeletesBackupAfterOneSecond()
    {
        using var fixture = new Fixture();

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.Confirmed, result);
        Assert.Equal("candidate", File.ReadAllText(fixture.Current));
        Assert.False(File.Exists(fixture.Backup));
        Assert.Equal(TimeSpan.FromSeconds(1), fixture.Operations.StabilityDelay);
        Assert.True(fixture.Operations.CandidateWasAliveAfterStabilityDelay);
        Assert.Empty(fixture.Operations.Restarts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCandidateExitsDuringStabilityWindow_RestoresBackup()
    {
        using var fixture = new Fixture();
        fixture.Operations.ExitDuringStabilityDelay = true;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(UpdateInstallResult.RolledBack, result);
        Assert.Equal("old", File.ReadAllText(fixture.Current));
        Assert.False(File.Exists(fixture.Backup));
    }

    [Fact]
    public async Task ExecuteAsync_SchedulesHelperCopyCleanupOnlyAfterHelperProcessCloses()
    {
        using var fixture = new Fixture();

        await fixture.ExecuteAsync();

        Assert.Equal((fixture.HelperCopy, Fixture.HelperProcessId), fixture.Operations.HelperCleanup);
        Assert.True(fixture.Operations.CleanupWaitsForHelperExit);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"InventoryTrackerInstallTests-{Guid.NewGuid():N}");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Current = Path.Combine(_directory, "target with 'quote'.exe");
            Download = Path.Combine(_directory, "downloaded candidate.exe");
            Backup = Path.Combine(_directory, "backup unique.exe");
            HelperCopy = Path.Combine(_directory, "helper copy.exe");
            File.WriteAllText(Current, "old");
            File.WriteAllText(Download, "candidate");
            File.WriteAllText(HelperCopy, "helper");
            Operations = new FakeOperations();
        }

        public string Current { get; }
        public string Download { get; }
        public string Backup { get; }
        public string HelperCopy { get; }
        public FakeOperations Operations { get; }
        public const int HelperProcessId = 99;

        public Task<UpdateInstallResult> ExecuteAsync() => UpdateInstallTransaction.ExecuteAsync(
            Current, Download, Backup, HelperCopy, parentProcessId: 7, helperProcessId: HelperProcessId,
            operations: Operations, timing: new UpdateInstallTiming(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)));

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeOperations : IUpdateInstallOperations
    {
        private const int CandidateProcessId = 42;
        private bool _stabilityDelayCompleted;

        public Func<string, string, bool>? FailMove { get; set; }
        public bool FailCandidateLaunch { get; set; }
        public bool FailRollbackLaunch { get; set; }
        public bool CandidateExited { get; set; }
        public bool ExitDuringStabilityDelay { get; set; }
        public bool ConfirmationTimedOut { get; set; }
        public bool ParentExited { get; set; } = true;
        public TimeSpan AcknowledgementElapsed { get; set; }
        public bool MissingAcknowledgement { get; set; }
        public string? AcknowledgementNonceOverride { get; set; }
        public int? AcknowledgementProcessIdOverride { get; set; }
        private string? _startedNonce;
        private long _elapsedTicks;
        public TimeSpan ConfirmationTimeout { get; private set; }
        public TimeSpan StabilityDelay { get; private set; }
        public bool CandidateWasAliveAfterStabilityDelay { get; private set; }
        public List<string> Deleted { get; } = [];
        public List<(string Path, string Argument)> Restarts { get; } = [];
        public (string Path, int ProcessId)? HelperCleanup { get; private set; }
        public bool CleanupWaitsForHelperExit { get; private set; }
        public List<(Exception Exception, string? RecoveryPath, bool RollbackFailed)> Failures { get; } = [];

        public bool FileExists(string path) => File.Exists(path);

        public void MoveFile(string source, string destination)
        {
            if (FailMove?.Invoke(source, destination) == true) throw new IOException("Injected move refusal.");
            File.Move(source, destination);
        }

        public void DeleteFile(string path)
        {
            Deleted.Add(path);
            File.Delete(path);
        }

        public UpdateInstallProcess StartCandidate(string path, string pipeName, string nonce)
        {
            if (FailCandidateLaunch) throw new InvalidOperationException("Injected launch refusal.");
            Assert.True(File.Exists(path));
            Assert.False(string.IsNullOrWhiteSpace(pipeName));
            Assert.False(string.IsNullOrWhiteSpace(nonce));
            _startedNonce = nonce;
            return new UpdateInstallProcess(CandidateProcessId, GetTimestamp());
        }

        public Task<UpdateInstallAcknowledgement?> WaitForAcknowledgementAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ConfirmationTimeout = timeout;
            _elapsedTicks += AcknowledgementElapsed.Ticks;
            UpdateInstallAcknowledgement? acknowledgement = MissingAcknowledgement || ConfirmationTimedOut
                ? null
                : new UpdateInstallAcknowledgement(AcknowledgementNonceOverride ?? _startedNonce!,
                    AcknowledgementProcessIdOverride ?? CandidateProcessId);
            return Task.FromResult(acknowledgement);
        }

        public Task<bool> WaitForParentExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(ParentExited);

        public long GetTimestamp() => _elapsedTicks;

        public TimeSpan GetElapsedTime(long timestamp) => TimeSpan.FromTicks(_elapsedTicks - timestamp);

        public bool HasExited(int processId) => processId == CandidateProcessId &&
            (CandidateExited || (ExitDuringStabilityDelay && _stabilityDelayCompleted));

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            StabilityDelay = delay;
            _elapsedTicks += delay.Ticks;
            _stabilityDelayCompleted = true;
            CandidateWasAliveAfterStabilityDelay = !HasExited(CandidateProcessId);
            return Task.CompletedTask;
        }

        public void StopCandidate(int processId) { }

        public void StartRollback(string path, string argument)
        {
            if (FailRollbackLaunch) throw new InvalidOperationException("Injected rollback launch refusal.");
            Restarts.Add((path, argument));
        }

        public void ScheduleHelperCleanupAfterExit(string path, int processId)
        {
            HelperCleanup = (path, processId);
            CleanupWaitsForHelperExit = true;
        }

        public void ReportFailure(Exception exception, string? recoveryPath = null, bool rollbackFailed = false) =>
            Failures.Add((exception, recoveryPath, rollbackFailed));
    }
}
