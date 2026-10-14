using InventoryTracker.App;
using InventoryTracker.Data;
using InventoryTracker.Ingest;
using InventoryTracker.Naming;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests;

public sealed class TrackerOptionsTests : IDisposable
{
    private const string Store =
        "<2026-08-27T00:40:00.000Z> [Notice] <InventoryManagementRequest> Queued Request[1] " +
        "Type[Store] for 'Pilot' [2001] Source Inventory[INVALID] " +
        "Target Inventory[204821708183:Location:4005457614]. " +
        "Source[NONE] amount[1] rank[0]. Target[NONE] amount[0] rank[0]. " +
        "Item[grin_multitool_01_red01_481104483777] action[none]";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"itrk-options-{Guid.NewGuid():N}");
    private readonly TrackerDb _db;
    private readonly TrackerOptions _options;
    private readonly TrackerState _state;
    private readonly LogWatchService _watcher;

    public TrackerOptionsTests()
    {
        Directory.CreateDirectory(_dir);
        var initialLogs = Path.Combine(_dir, "initial-logs");
        Directory.CreateDirectory(initialLogs);
        _db = new TrackerDb(Path.Combine(_dir, "tracker.db"));
        _db.Initialize();
        _options = new TrackerOptions { LogDir = initialLogs, DatabasePath = _db.Path };
        var names = new WikiNameService(_db, new HttpClient());
        _state = new TrackerState(_db, names, _options);
        _watcher = new LogWatchService(
            _options, _db, _state, names, new PlaceNameService(_db, _options, new HttpClient()),
            NullLogger<LogWatchService>.Instance);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Concurrent_source_applications_keep_config_source_parser_and_diagnostics_together()
    {
        var configPath = Path.Combine(_dir, "config.json");
        var firstDir = Path.Combine(_dir, "first-logs");
        var secondDir = Path.Combine(_dir, "second-logs");
        Directory.CreateDirectory(firstDir);
        Directory.CreateDirectory(secondDir);
        File.WriteAllText(Path.Combine(firstDir, "Game.log"), Store + "\n");
        File.WriteAllText(Path.Combine(secondDir, "Game.log"), Store + "\n");
        SaveInitialConfig(configPath);

        var firstConfigSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondContended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSourceApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSecond = new ManualResetEventSlim();
        var firstHooks = new TrackerOptionsApplyHooks
        {
            AfterConfigurationSaved = () =>
            {
                firstConfigSaved.SetResult();
                releaseFirst.Wait();
            },
        };
        var secondHooks = new TrackerOptionsApplyHooks
        {
            BeforeGate = secondStarted.SetResult,
            OnApplyContended = secondContended.SetResult,
            AfterSourceApplied = secondSourceApplied.SetResult,
            BeforeResetAsync = () =>
            {
                secondResetStarted.SetResult();
                releaseSecond.Wait();
                return Task.CompletedTask;
            },
        };

        var first = Task.Run(() => _options.ApplyLogSourceAsync(
            firstDir, null, _watcher, markSetupComplete: false,
            configPath: configPath, hooks: firstHooks));
        await firstConfigSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondDate = new DateOnly(2026, 9, 1);
        var second = Task.Run(() => _options.ApplyLogSourceAsync(
            secondDir, secondDate, _watcher, markSetupComplete: false,
            configPath: configPath, hooks: secondHooks));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            var checkpoint = await Task.WhenAny(secondSourceApplied.Task, secondContended.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (checkpoint == secondContended.Task)
            {
                // A correct application gate excludes B until A has completed its save,
                // mutation and reset. B then applies as the later complete generation.
                releaseFirst.Set();
                await first;
                await secondResetStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                releaseSecond.Set();
                await second;
            }
            else
            {
                // Without the gate, B reaches its reset while A is paused after saving.
                // Let A mutate/reset first, then B resumes against A's source: config and
                // runtime state no longer identify the same complete application.
                await secondResetStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                releaseFirst.Set();
                await first;
                releaseSecond.Set();
                await second;
            }
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
        }

        var source = _options.CaptureLogSource();
        var firstDate = (DateTimeOffset?)null;
        var secondDateValue = new DateTimeOffset(secondDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var isFirst = source == (firstDir, firstDate);
        var isSecond = source == (secondDir, secondDateValue);

        Assert.True(isFirst || isSecond);
        var config = AppConfig.Load(configPath);
        Assert.Equal(source.LogDir, config.LogDir);
        Assert.Equal(source.InceptionDate, config.InceptionDate);
        Assert.Equal(_db.Path, config.DatabasePath);
        Assert.Equal(5730, config.Port);
        Assert.False(config.SetupComplete);
        var expectedMoves = isFirst ? 1 : 0;
        Assert.Equal((long)expectedMoves, _state.Current.Health.Moves);
        Assert.Equal(source.LogDir, _state.Current.Health.LogDir);
        Assert.Equal(source.InceptionDate, _state.Current.Health.InceptionDate);
        var lastPass = Assert.IsType<IngestPass>(_watcher.LastPass);
        Assert.Equal(expectedMoves, lastPass.MovesInserted);
        Assert.Null(lastPass.Error);
        if (isFirst) Assert.NotEmpty(_state.Current.Holdings);
        else Assert.Empty(_state.Current.Holdings);
    }

    [Fact]
    public async Task Invalid_log_directory_is_rejected_before_the_store_is_cleared()
    {
        var configPath = Path.Combine(_dir, "config.json");
        var initialLogs = _options.LogDir;
        SaveInitialConfig(configPath);
        File.WriteAllText(Path.Combine(initialLogs, "Game.log"), Store + "\n");
        await _watcher.ResetAndRebuildAsync();
        Assert.NotEmpty(_state.Current.Holdings);

        var invalidLogPath = Path.Combine(_dir, "this-is-a-file");
        File.WriteAllText(invalidLogPath, "not a directory");
        var before = AppConfig.Load(configPath);
        var beforeHealth = _state.Current.Health;

        var error = await Record.ExceptionAsync(() => _options.ApplyLogSourceAsync(
            invalidLogPath, new DateOnly(2026, 9, 1), _watcher, configPath: configPath));

        Assert.NotNull(error);
        Assert.Equal(initialLogs, _options.LogDir);
        Assert.Null(_options.InceptionDate);
        Assert.NotEmpty(_state.Current.Holdings);
        Assert.Equal(before.LogDir, AppConfig.Load(configPath).LogDir);
        Assert.Equal(before.InceptionDate, AppConfig.Load(configPath).InceptionDate);
        Assert.Equal(beforeHealth.LogDir, _state.Current.Health.LogDir);
        Assert.Equal(beforeHealth.InceptionDate, _state.Current.Health.InceptionDate);
    }

    [Fact]
    public async Task Apply_failure_after_source_mutation_is_explicit_and_recoverable()
    {
        var initialLogs = _options.LogDir;
        File.WriteAllText(Path.Combine(initialLogs, "Game.log"), Store + "\n");
        await _watcher.ResetAndRebuildAsync();
        Assert.NotEmpty(_state.Current.Holdings);

        var configPath = Path.Combine(_dir, "config.json");
        SaveInitialConfig(configPath);
        var replacementLogs = Path.Combine(_dir, "replacement-logs");
        Directory.CreateDirectory(replacementLogs);
        File.WriteAllText(Path.Combine(replacementLogs, "Game.log"), Store + "\n");
        var replacementDate = new DateOnly(2026, 9, 1);
        var replacementDateValue = new DateTimeOffset(replacementDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var injected = new InvalidOperationException("injected before reset");
        var hooks = new TrackerOptionsApplyHooks
        {
            BeforeResetAsync = () => Task.FromException(injected),
        };

        var error = await Record.ExceptionAsync(() => _options.ApplyLogSourceAsync(
            replacementLogs, replacementDate, _watcher, markSetupComplete: false,
            configPath: configPath, hooks: hooks));

        Assert.Same(injected, error);
        Assert.Equal(replacementLogs, _options.LogDir);
        Assert.Equal(replacementDateValue, _options.InceptionDate);
        Assert.NotEmpty(_state.Current.Holdings);
        Assert.Equal(replacementLogs, AppConfig.Load(configPath).LogDir);
        Assert.Equal(replacementDateValue, AppConfig.Load(configPath).InceptionDate);

        await _options.ApplyLogSourceAsync(
            replacementLogs, replacementDate, _watcher, configPath: configPath);
        Assert.Empty(_state.Current.Holdings);
        Assert.Equal(replacementLogs, _state.Current.Health.LogDir);
        Assert.Equal(replacementDateValue, _state.Current.Health.InceptionDate);
    }

    [Fact]
    public async Task Rebuild_ingestion_and_source_application_complete_without_deadlock()
    {
        var configPath = Path.Combine(_dir, "config.json");
        var replacementLogs = Path.Combine(_dir, "replacement-logs");
        Directory.CreateDirectory(replacementLogs);
        File.WriteAllText(Path.Combine(replacementLogs, "Game.log"), Store + "\n");
        SaveInitialConfig(configPath);

        using var start = new Barrier(4);
        var apply = Task.Run(async () =>
        {
            start.SignalAndWait();
            await _options.ApplyLogSourceAsync(replacementLogs, null, _watcher, configPath: configPath);
        });
        var ingest = Task.Run(async () =>
        {
            start.SignalAndWait();
            await _watcher.RunPassAsync(IngestTrigger.Poll);
        });
        var rebuild = Task.Run(() =>
        {
            start.SignalAndWait();
            _state.Rebuild();
        });

        start.SignalAndWait();
        await Task.WhenAll(apply, ingest, rebuild).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(replacementLogs, _options.LogDir);
        Assert.Equal(replacementLogs, AppConfig.Load(configPath).LogDir);
    }

    private void SaveInitialConfig(string path) => new AppConfig
    {
        LogDir = _options.LogDir,
        DatabasePath = _db.Path,
        Port = AppConfig.DefaultPort,
        SetupComplete = false,
    }.Save(path);
}
