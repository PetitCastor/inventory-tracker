# Full code review — 2026-08-07

Scope: all 49 git-tracked files at `65f5524`/`c538653`. Sources, project/build files, CI,
publish script, folder structure and naming.

The project builds clean — `dotnet build -c Release` reports **0 errors, 0 warnings**. Nothing
below is a compiler complaint; every item required reading the code.

Claims that could be checked mechanically were checked. Where a suspicion did not survive
verification it was dropped rather than reported — see [Checked and cleared](#checked-and-cleared).

## Summary

| Severity | Count |
|---|---|
| High | 6 |
| Medium | 11 |
| Low / naming / hygiene | 16 |

The codebase is unusually well commented, and the comments carry real reasoning about the
domain rather than restating the code. The heuristics in `LedgerReplay` and `HoldingResolver`
are documented to a standard most projects never reach. The problems below are concentrated in
three places: the **host/tray lifecycle**, **concurrency between the UI and the background
ingest**, and **build/CI hygiene**. The parsing and resolution core is the strongest part of
the project.

The single largest gap is structural rather than any one defect: **there is no test project at
all**, in an application whose entire value is inference heuristics over a log grammar.

---

## High

### H1. Tray "Refresh item names" handler is `async void` — a network error kills the process

`src/App/TrayHost.cs:102-107`

```csharp
menu.Items.Add("Refresh item names from wiki", null, async (_, _) =>
{
    var names = app.Services.GetRequiredService<WikiNameService>();
    await names.RefreshAsync();
    app.Services.GetRequiredService<TrackerState>().Rebuild();
});
```

The lambda is converted to `EventHandler`, which returns `void`, so this is an `async void`
method. An exception thrown after the first `await` cannot be observed by any caller; it is
raised on the synchronization context and terminates the process.

`WikiNameService.RefreshAsync` performs up to ~62 HTTP requests to `api.star-citizen.wiki`
(`src/Naming/WikiNameService.cs:61-105`) and does not catch anything. Wiki down, DNS failure,
laptop offline, or the 60-second timeout elapsing all throw. `RefreshAsync` also calls
`ct.ThrowIfCancellationRequested()` and `doc.RootElement.GetProperty("data")`, which throws
`KeyNotFoundException` if the API response shape changes.

There is no safety net: `src/App/TrayHost.cs` installs no `Application.ThreadException`
handler, no `AppDomain.UnhandledException` handler, and no `Application.SetUnhandledExceptionMode`
(verified by grep across `src/` — no matches for any of them).

So the failure mode is: user clicks a tray menu item while offline, and the whole tracker
disappears with no message. The background path for the same operation is handled correctly —
`LogWatchService.EnsureCatalogueAsync` (`src/Ingest/LogWatchService.cs:88-109`) wraps the
identical call in `try`/`catch` and logs a warning. The tray path just never got the same
treatment.

**Fix:** wrap the body in `try`/`catch`, or make the handler delegate to a task-returning method
whose exceptions are logged. Consider also registering a global handler so no future `async void`
can take the app down silently.

### H2. The `VerifyBlazorScriptEmbedded` build guard is dead — it can never fire

`src/InventoryTracker.csproj:44-49`

```xml
<Target Name="VerifyBlazorScriptEmbedded" BeforeTargets="CoreCompile">
  <Error Condition="'@(EmbeddedResource)' == ''" Text="blazor.web.js was not found ..." />
</Target>
```

The comment above it states the intent exactly: *"Embedding is easy to lose silently: the app
would still serve pages, they would just never respond to input. Fail the build instead."*
That is a good instinct, but the condition tests the wrong thing.

`@(EmbeddedResource)` is the *whole* item list, and `src/InventoryTracker.csproj:17-19`
unconditionally adds the application icon to it:

```xml
<ItemGroup>
  <EmbeddedResource Include="..\assets\InventoryTracker.ico" LogicalName="app.ico" />
</ItemGroup>
```

So the list always has at least one item and the condition is always false.

Verified by evaluating the target directly:

```
dotnet msbuild src/InventoryTracker.csproj -t:VerifyBlazorScriptEmbedded -getItem:EmbeddedResource
```

which returns two items — `InventoryTracker.ico` (LogicalName `app.ico`) and
`blazor.web.js` from `microsoft.aspnetcore.app.internal.assets/10.0.9`. If the NuGet glob
ever stops matching, the icon alone keeps the guard quiet and the build succeeds with a
non-interactive UI — precisely the outcome the target exists to prevent.

**Fix:** test the specific item, not the list. For example condition on an item filtered by
`LogicalName == 'blazor.web.js'`, or check that the glob's source directory resolved.

### H3. Host startup blocks on a full log backfill

`src/Ingest/LogWatchService.cs:62-86`

```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    Ingest();          // synchronous, before any await
    StartWatching();
    ...
```

`BackgroundService.StartAsync` invokes `ExecuteAsync` and only returns to the host once the
returned task hits its first `await`. Everything before that first `await` — here a complete
synchronous `Ingest()` over every rotated log file — runs inline on the startup thread.

Consequences, in order:

1. `app.Start()` in `src/App/TrayHost.cs:81` does not return until the backfill finishes.
2. The tray icon (`:83`) is not created until then.
3. `OpenBrowser` (`:85`) fires late, and the browser may reach the port before Kestrel is
   listening.

On a first run against a full `logbackups/` directory this is the difference between a tray
app appearing instantly and one that appears to hang.

**Fix:** `await Task.Yield();` as the first statement of `ExecuteAsync`, or move the initial
pass into the loop body so it happens after the service has yielded.

### H4. `ResetIngest()` is called outside the ingest lock

`src/Components/Pages/Settings.razor:171-185` and `src/Components/Pages/Setup.razor:96-97`

`LogWatchService` deliberately serialises ingest passes with `_ingestLock`
(`src/Ingest/LogWatchService.cs:36`), documented as *"Serialises ingest passes so a 'rebuild
now' and the background loop never hit the store at the same time."*

But `Db.ResetIngest()` is invoked directly on `TrackerDb` from both pages and never takes that
lock:

```csharp
// Settings.razor
SaveSettings();
Db.ResetIngest();          // not under _ingestLock
await Watcher.RebuildNowAsync();
```

```csharp
// Setup.razor
Db.ResetIngest();          // not under _ingestLock
Watcher.RequestScan();
```

The background loop wakes on a `FileSystemWatcher` event or every 2 minutes on the sweep timer.
If a pass is in flight when the user clicks "Save and rebuild", the `DELETE FROM ...` statements
in `ResetIngest` (`src/Data/TrackerDb.cs:87-98`) interleave with that pass's inserts. The
surviving rows are whatever the in-flight transaction commits after the delete — including
`session` rows whose `byte_offset` watermark now points past data that was just deleted, which
means those lines are never re-read.

Setup.razor is the more likely of the two to bite, because it does not even wait for the rebuild
— it calls `RequestScan()` and immediately navigates away.

**Fix:** put the reset behind the same lock, e.g. expose a `LogWatchService.ResetAndRebuildAsync()`
that takes `_ingestLock`, calls `db.ResetIngest()`, and then ingests.

### H5. Log rotation is only detected when the file shrinks

`src/Ingest/ByteLineReader.cs:46-49`

```csharp
// The file shrank, so it was rotated or rewritten under us. Start over.
if (fs.Length < Offset)
{
    Reset();
}
```

The byte watermark is persisted per path in the `session` table, and `Game.log` always has the
same path. Rotation is inferred purely from the file being shorter than the stored offset.

That holds while the app is running. It does not hold across a restart. If the tracker is closed
at watermark 5 MB, the game rotates `Game.log` to `logbackups/` and writes a fresh one, and the
player then plays long enough for the new `Game.log` to exceed 5 MB before the tracker is
launched again, then `fs.Length >= Offset` and no reset happens. Ingestion resumes at byte
5,000,000 of an unrelated file.

Two things go wrong at once: every line before that offset is never parsed, and the byte offsets
recorded as `move.line_offset` — which the schema comment at `src/Data/TrackerDb.cs:118-121`
identifies as the row's stable identity — now collide with offsets from the previous file under
the same `session_id`. Because `InsertMove` uses `INSERT OR IGNORE` on
`UNIQUE(session_id, line_offset, item_ix)` (`src/Ingest/LogIngestor.cs:459-467`), those
collisions are silently discarded as duplicates.

**Fix:** store an identity for the live file alongside the offset — creation time
(`File.GetCreationTimeUtc`) is the cheap option, or the first line's timestamp — and reset when
it changes rather than only when the length regresses.

### H6. A malformed `config.json` is silently replaced with defaults

`src/App/AppConfig.cs:45-70`

```csharp
public static AppConfig Load(string? path = null)
{
    path ??= DefaultPath;
    var config = File.Exists(path) ? ReadOrDefault(path) : new AppConfig();

    var changed = !File.Exists(path);
    if (config.LogDir is null) { config.LogDir = LogFileLocator.DefaultLogDir; changed = true; }
    ...
    if (changed) config.Save(path);
```

`ReadOrDefault` catches `JsonException`/`IOException` and returns a blank `AppConfig` after
printing to `Console.Error` — which in the tray app (a `WinExe`) goes nowhere, since the console
is only attached on the CLI path (`src/Program.cs:22-27`).

The blank config then has null `LogDir`/`DatabasePath`/`Port`, so `changed` flips to true and
`config.Save(path)` **overwrites the user's file with defaults**. `InceptionDate` and
`SetupComplete` are not defaulted, so they are silently lost: `SetupComplete` reverts to `false`
and the user is dropped back into the setup wizard with their inception date gone.

A single stray character in `config.json` — a half-finished manual edit, a power cut during
`File.WriteAllText` at `src/App/AppConfig.cs:78`, which is not atomic — is enough to trigger it.

**Fix:** on a parse failure, rename the bad file aside (`config.json.corrupt`) rather than
overwriting it, and surface the problem in the UI. Write via temp-file + `File.Replace` so a
crash mid-write cannot truncate the original.

---

## Medium

### M1. No single-instance guard on a fixed port

`src/App/TrayHost.cs:81`, `src/App/TrackerOptions.cs:13`

The port is deliberately fixed at 5730 so the bookmark keeps working — a reasonable decision,
documented at `TrackerOptions.cs:12`. But nothing stops a second instance launching, and
`app.Start()` will throw `IOException: Failed to bind to address http://localhost:5730` when it
does. `Run` has no `try`/`catch`, the process is a `WinExe`, and no console is attached on the
tray path, so the second launch dies with no visible message. Double-clicking the tray app twice
— or launching it while it is already minimised to the tray — looks like nothing happening.

**Fix:** a named `Mutex` at startup; if already held, focus/open the browser at the existing
instance's URL and exit cleanly.

### M2. Completions can be applied to stale moves

`src/Ingest/LogIngestor.cs:220-226`, with `src/Ingest/LogIngestor.cs:399-419`

`PreloadPendingRequests` restores up to 500 unresolved moves into `state.MoveByRequest` when
resuming mid-file. `MoveCompleted` then writes its result to every entry under the matching
request number:

```csharp
case MoveCompleted done:
    if (state.MoveByRequest.TryGetValue(done.RequestNo, out var moves))
    {
        foreach (var m in moves) UpdateResult(cn, tx, m.Id, done.Result);
```

There is no time-window check. Elsewhere in the same file the code is careful about exactly this
hazard — geid pairing guards with `GeidPairingWindow` (`:191`, `:332`) and the comment at `:52-56`
explains why: *"Request numbers restart per file and again on every shard change."*

The same reasoning applies here and the guard is missing. A preloaded move from early in the file
keeps its slot until some newer move reuses the number (`:344` removes it), so a completion
arriving before that reuse stamps a result onto an unrelated, much older row.

The 500-row `LIMIT` is also an undocumented magic number; beyond it, completions go unmatched.

**Fix:** apply the same pairing window when resolving completions, and name the 500 constant.

### M3. Machine-specific default log directory ships in releases

`src/Ingest/LogFileLocator.cs:15`

```csharp
public const string DefaultLogDir = @"E:\Games\StarCitizen\LIVE";
```

This is one developer's drive layout baked in as the product default, and it reaches users —
CI publishes this to a GitHub Release on every merge to `main`. It is also duplicated as a
placeholder in two `.razor` files (`Settings.razor:19`, `Setup.razor:19`).

The setup wizard means it is recoverable, and it is the pre-filled value the wizard offers, so
the practical cost is a wrong first suggestion rather than a broken app. Still worth probing the
usual locations (`%ProgramFiles%\Roberts Space Industries\StarCitizen\LIVE`, the launcher's own
config) and falling back to empty.

### M4. No tests anywhere

There is no test project in the solution (`InventoryTracker.slnx` declares exactly one project)
and `.github/workflows/ci.yml` runs `restore` + `build` only.

This is the most consequential structural gap. The project is essentially a parser and an
inference engine:

- `InventoryEventParser` — 13 regexes over an undocumented, changing log grammar
- `LedgerReplay` — order-dependent fold with anonymous/named reconciliation
- `HoldingResolver` — multi-hop chain walking with confidence arithmetic
- `PlaceCatalog` — alias grouping and vote tallying

Every one of these is pure, deterministic, and takes plain inputs — they are unusually easy to
test. The `memory/sc-*.md` notes already contain real log lines that would serve as fixtures.

Nothing catches a regression here today. A regex tweak that silently stops matching would show
up as "my stuff vanished from the UI", with no failing build.

**Suggested first targets:** `InventoryEventParser.Parse` against captured lines,
`LedgerReplay.Run` over hand-built move sequences, `PlaceCatalog.SystemFromRaw` (already
`internal`, so an `InternalsVisibleTo` is all it needs), and `EntityId.TrySplit`.

### M5. CI grants `contents: write` to the whole workflow

`.github/workflows/ci.yml:9-11`

```yaml
permissions:
  contents: write
```

Workflow-level `permissions` apply to every job, so the `build` job — which runs on every pull
request, including from forks — gets a token that can write to the repository. It only needs
`contents: read`.

**Fix:** default the workflow to `contents: read` and grant `contents: write` on the `publish`
job only.

### M6. Third-party action pinned to a mutable tag

`.github/workflows/ci.yml:78`

```yaml
uses: softprops/action-gh-release@v2
```

`v2` is a moving tag. Combined with M5, whatever `v2` points to at run time executes with a
write-capable token. Pin to a full commit SHA.

### M7. `WikiNameService._cache` is mutated across threads without synchronisation

`src/Naming/WikiNameService.cs:55`, `:102`, `:151-170`

The service is registered as a singleton, and `src/App/TrayHost.cs:39-41` documents that this is
deliberate: *"the service caches the whole catalogue in memory, so it has to be a singleton."*

`_cache` is a plain non-volatile field. It is written by `Cache()` from Blazor circuit threads
(`Item.razor:68`, `Settings.razor`), written by `TrackerState.Rebuild()` on the background ingest
thread (`src/App/TrackerState.cs:46`), and set to `null` by `RefreshAsync` on yet another
(`:102`).

Reference assignment is atomic and the dictionary is fully built before publication, so this will
not tear. The real effects are milder: concurrent first-callers each build a full ~12k-entry
dictionary, and a refresh landing between the null check and the return means a caller keeps
using the stale catalogue. Worth making explicit rather than leaving to chance.

**Fix:** `volatile`, or a `Lazy<Dictionary<...>>` swapped wholesale on refresh.

### M8. Folder picker blocks two threads and has no owner window

`src/App/DesktopShell.cs:18-43`, called from `Settings.razor:109` and `Setup.razor:60`

```csharp
var picked = await Task.Run(() => Shell.PickFolder(_logDir));   // caller
...
thread.Start();
thread.Join();                                                   // PickFolder
```

`Task.Run` occupies a thread-pool thread, which then blocks on `thread.Join()` waiting for a
modal dialog. Both are held until the user responds.

`dialog.ShowDialog()` is called with no owner, so the folder browser is not modal to anything and
is not guaranteed to come to the front. The user is looking at a browser window; the dialog can
open behind it, with the page apparently frozen and nothing indicating why. There is no timeout
and no cancellation — if the user never finds the dialog, the thread is held for the life of the
process.

**Fix:** pass an owner (the tray app's hidden form, or `TopMost = true` on a temporary owner form)
so the dialog reliably surfaces.

### M9. Settings diagnostics go stale after a rebuild

`src/Components/Pages/Settings.razor:171-185`

```csharp
await Watcher.RebuildNowAsync();

_holdings = State.Current.Holdings;   // refreshed
_rebuilding = false;
_rebuilt = true;
```

`_unnamedLocations`, `_unknownContainers` and `_unnamedItems` are computed only in
`OnInitialized` (`:123-144`). After "Save and rebuild from logs" the page shows the new
"Tracked instances" count next to three diagnostic lists describing the *previous* store —
including their headline counts, e.g. "Unnamed stations (12)". Nothing signals they are stale.

**Fix:** extract the derivation into one method and call it from both `OnInitialized` and after
the rebuild.

### M10. Two of three pages never live-update

`src/Components/Pages/Item.razor`, `src/Components/Pages/Settings.razor`

`TrackerState` documents the contract at `src/App/TrackerState.cs:15-17`: *"pages subscribe to
`Changed` so tailing the live log updates an open browser without a refresh."*

Only `Place.razor` honours it — it subscribes in `OnInitialized` (`:110`), implements
`IDisposable`, and unsubscribes (`:287`). `Item.razor` reads `State.Current` in
`OnParametersSet` and never subscribes. `Settings.razor` reads it in `OnInitialized` and never
subscribes.

Both are `@rendermode InteractiveServer`, so a user can sit on an item page watching a stale
placement while the tracker ingests the move that relocated it.

### M11. `Cache=Shared` with no busy timeout, under concurrent access

`src/Data/TrackerDb.cs:20-25`, `:34-40`

```csharp
_connectionString = new SqliteConnectionStringBuilder
{
    DataSource = path,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared,
}.ToString();
```

Writers here are the background ingest loop and the UI ("Save and rebuild", `ResetIngest`);
readers are `HoldingResolver.Load`, `PlaceCatalog.Load`, `WikiNameService.Cache`, and the CLI.

Shared-cache mode moves contention from the file level to table-level locks within the process
and can surface as `SQLITE_LOCKED` — which, unlike `SQLITE_BUSY`, is not resolved by the busy
handler. No `DefaultTimeout` or `busy_timeout` pragma is set (`Open()` sets only `journal_mode`,
`synchronous` and `foreign_keys`).

Microsoft's guidance is that shared cache is intended mainly for in-memory databases; for a
file-backed store with WAL it is usually the wrong default. WAL already gives concurrent
readers alongside one writer.

**Fix:** drop `Cache = Shared` and set a `busy_timeout` (or `SqliteConnectionStringBuilder.DefaultTimeout`).

---

## Low, naming and hygiene

### L1. `Cli.cs` has a UTF-8 BOM and mojibake — the only file in the repo with either

`src/Cli.cs:144`, `:249`

```csharp
/// <summary>Groups every inferred placement by where it ends up â€” the Find view, in text.</summary>
/// <summary>Prints one item instance's full movement history â€” the audit view.</summary>
```

`â€”` is an em-dash that was decoded as CP1252 and re-encoded as UTF-8. Every other file in the
repo uses a real `—` correctly (`LogIngestor.cs:175`, `HoldingResolver.cs:194`, and many more).

`file` across all tracked sources reports `Cli.cs` as the sole `UTF-8 (with BOM)` file; the first
three bytes are `ef bb bf`. Both facts point at one editor round-trip with the wrong encoding.

**Fix:** repair the two dashes and re-save without a BOM, matching the other 48 files.

### L2. The `SCLogParser` name survives the rename in four places

Reported, not changed, per your instruction — noted here so it is on the record.

- `src/Data/TrackerDb.cs:31` — `%LOCALAPPDATA%\SCLogParser\tracker.db`
- `src/App/AppConfig.cs:31` — the same folder as the config fallback
- `src/App/TrayHost.cs:45` — `User-Agent: SCLogParser/1.0 (inventory tracker)`
- `src/Cli.cs:96` — the same User-Agent string, duplicated

The storage path is the one with a migration cost. The two User-Agent strings have none — they
are outbound identification to `api.star-citizen.wiki` and currently misidentify the client.

### L3. Dead code — nine unused members

Each verified unreferenced across `src/**/*.cs` and `src/**/*.razor`:

| Member | Location |
|---|---|
| `wiki_item_fts` table | `src/Data/TrackerDb.cs:215` |
| `PlaceCatalog.Empty` | `src/Naming/PlaceCatalog.cs:59` |
| `Holding.Nowhere` | `src/Resolve/HoldingModel.cs:42` |
| `MoveRecord.IsInstanceLevel` | `src/Resolve/HoldingModel.cs:33` |
| `ItemHolding.Container` | `src/Resolve/HoldingModel.cs:79` |
| `LogFileLocator.LiveLogPath` | `src/Ingest/LogFileLocator.cs:72` |
| `HoldingResolver.LocationNames` | `src/Resolve/HoldingResolver.cs:54` |
| `ByteLineReader.Pending` | `src/Ingest/ByteLineReader.cs:23` |
| `ContainerInfo.LastSeenAt` | `src/Resolve/HoldingResolver.cs:33` |

`wiki_item_fts` is the one that costs something. It is a full FTS5 virtual table, dropped and
repopulated from `wiki_item` on every catalogue refresh by `RebuildSearchIndex`
(`src/Naming/WikiNameService.cs:230-243`), and **no code ever queries it**. Confirmed against
the live database: `SELECT COUNT(*) FROM wiki_item_fts` returns **12,282** rows, exactly
matching `wiki_item`. Its schema comment says *"the user types a human name, we need class
names"* — a search feature that was never built. Either build it or drop the table.

`ByteLineReader.Pending` is referenced only by its own doc comment on `:11`.

### L4. Two summary lines report the same number under different labels

`src/Cli.cs:211` and `src/Cli.cs:225`

```csharp
Console.WriteLine($"Distinct items    : {Scalar(cn, "SELECT COUNT(DISTINCT item_geid) FROM move")}");
...
Console.WriteLine($"Distinct instances: {Scalar(cn, "SELECT COUNT(DISTINCT item_geid) FROM move WHERE item_geid IS NOT NULL")}");
```

`COUNT(DISTINCT col)` already excludes nulls, so the `WHERE` clause changes nothing and the two
lines are identical by construction.

Confirmed against the live database — both return **69** (of 137 total moves, 30 have a null
`item_geid`).

**Fix:** drop one, or make "Distinct instances" mean something different from "Distinct items".

### L5. The same rule is spelled two different ways

`src/Cli.cs:209` vs `src/Resolve/HoldingModel.cs:25-26`

```sql
SELECT COUNT(*) FROM move WHERE result IN ('Succeed','succeed')
```

```csharp
public bool Succeeded => Result is not null && Result.Equals("succeed", StringComparison.OrdinalIgnoreCase);
```

The SQL enumerates two casings because SQLite's `=` is case-sensitive; the C# does it properly.
A third casing from the game breaks the CLI summary while the resolver keeps working — a
divergence that would be confusing to debug.

For the record, the live database currently contains only `succeed` and `NULL` in `move.result`.

**Fix:** `WHERE LOWER(result) = 'succeed'`, or better, route both through one definition.

### L6. `Place.razor` is the browse page, and collides with the domain type

`src/Components/Pages/Place.razor:1-7`

```razor
@page "/"
@* The generated component class is also called Place, so the domain type needs a name here. *@
@using NamedPlace = InventoryTracker.Naming.Place
```

The file is named after `Naming.Place`, but it is not a place-detail page — it is the
application's home/browse screen, routed at `/`, titled "Browse", and linked as "Browse" in the
nav (`MainLayout.razor:8`). The collision is real enough that the file has to alias the domain
type it shadows.

`Browse.razor` or `Home.razor` would remove the alias and describe the page.

### L7. Stale doc comment — `DesktopShell` describes a method that no longer exists

`src/App/DesktopShell.cs:5-10`

> *"The small amount of native desktop interaction the Blazor UI needs: showing a folder picker
> **and revealing a folder in Explorer**."*

The class has exactly one method, `PickFolder`. The Explorer-reveal helper was removed; its
description was not.

### L8. Misleading csproj comment — describes an approach the file does not take

`src/InventoryTracker.csproj:36-38`

> *"`BlazorFrameworkStaticWebAssetRoot` comes from the framework assets package and is only set
> once NuGet's .targets are imported, so this has to run as a target."*

The `ItemGroup` it annotates (`:39-42`) is at project scope, **not** inside a `<Target>`, and
`BlazorFrameworkStaticWebAssetRoot` is never referenced anywhere in the file — the glob uses
`$(NuGetPackageRoot)` instead. The comment documents an earlier design. Points 1 and 2 above it
are accurate and worth keeping.

### L9. Defaults duplicated across three files

Port `5730` is written literally in three places:

- `src/App/AppConfig.cs:53` — `config.Port = 5730`
- `src/App/TrackerOptions.cs:13` — `public int Port { get; set; } = 5730`
- `src/App/TrackerOptions.cs:38` — `Port = config.Port ?? 5730`

`LogFileLocator.DefaultLogDir` and `TrackerDb.DefaultPath` are likewise defaulted independently
in both `AppConfig.Load` (`:51-52`) and `TrackerOptions.Load` (`:34-35`). Three copies of a
default is three chances to change two of them.

### L10. `launchSettings.json` URLs are dead configuration

`src/Properties/launchSettings.json:9`

```json
"applicationUrl": "https://localhost:58132;http://localhost:58133"
```

`src/App/TrayHost.cs:28` calls `builder.WebHost.UseUrls(options.Url)`, which takes precedence
over the `applicationUrl` from launch settings. The app always listens on `http://localhost:5730`.

Since `launchBrowser` is `true`, pressing F5 in Visual Studio opens `https://localhost:58132`,
which nothing is serving. The profile also sets `ASPNETCORE_ENVIRONMENT=Development`, which the
app never reads.

**Fix:** set `applicationUrl` to `http://localhost:5730`, or drop it and set `launchBrowser: false`
since `TrayHost` opens the browser itself (`:85`).

### L11. `publish.ps1` and `ci.yml` duplicate the publish arguments, and disagree

`publish.ps1:67-82` and `.github/workflows/ci.yml:57-70` carry the same eight `-p:` switches.
They have already drifted: CI passes `-p:Version=0.1.<run_number>`, the local script passes no
version at all, so a locally published exe reports `1.0.0`.

**Fix:** have the workflow call `publish.ps1` so there is one definition.

### L12. Release version numbers skip

`.github/workflows/ci.yml:50-53`

```yaml
run: echo "version=0.1.${{ github.run_number }}" >> "$GITHUB_OUTPUT"
```

`github.run_number` increments for *every* run of the workflow, including pull-request runs that
never publish. Released versions therefore have gaps, and the patch number reflects CI activity
rather than the number of releases. It is also reset if the workflow file is renamed, which would
cause tag collisions with existing releases.

The comment explains the constraint honestly (avoiding a version bump commit into a protected
branch). `github.run_number` is a reasonable escape hatch; the gaps are just worth knowing about.

### L13. Duplicated heading in Settings

`src/Components/Pages/Settings.razor:13-15`

```razor
<h1>Settings</h1>

<h2>Settings</h2>
```

The `<h2>` labels the log-dir/inception fields. "Log source" or similar would say something; as
written it repeats the page title one line down.

### L14. `scu` should be `SCU` in the document title

`src/Components/Root.razor:7`

```html
<title>Inventory Tracker - Every station. Every scu box. Tracked.</title>
```

SCU (Standard Cargo Unit) is capitalised everywhere else in the codebase — `HoldingResolver.cs:459`
emits `"{scu:0.##} SCU container"`, the schema comment at `TrackerDb.cs:166` says "2 SCU crate",
and `README.md:3` says "2 SCU box". This is the one user-visible string that lowercases it.

### L15. Empty location names are not guarded, unlike empty place names

`src/Ingest/InventoryEventParser.cs:334-338`

```csharp
private static InventoryEvent? ParseLocationName(LogLine line)
{
    var m = LocationName().Match(line.Rest);
    return m.Success ? new LocationNamed(line.Timestamp, m.Groups["name"].Value) : null;
}
```

The regex is `Location\[(?<name>[^\]]*)\]` — `*`, so `Location[]` matches with an empty name and
propagates to `UpsertLocationName`, binding a location id to `""` and locking out the correct
name later via the conflict check at `LogIngestor.cs:632`.

`ParseRouteStart` twelve lines above does guard this (`:277`): `return name.Length == 0 ? null : ...`.

Currently harmless — the live database has zero empty `location_name` rows — but the asymmetry is
unintentional. Either tighten the regex to `+` or add the same guard.

### L16. `memory/` is agent scratch tracked at the repository root

Eight files (`memory/MEMORY.md`, `memory/sc-*.md`) are committed at the top level. The content is
genuinely valuable — `sc-log-grammar.md`, `sc-item-movement.md` and `sc-parse-cutoff-49.md` are
the reverse-engineering notes the parser is built from, and they are the natural fixture source
for the tests recommended in M4.

But `memory/` reads as tooling state rather than documentation, and it sits beside `src/` and
`assets/` as a peer. `docs/` would place it as what it is: the domain research behind the
heuristics.

Note also that `.gitignore` lists `package.json` and `package-lock.json` (`:10-11`) — ignoring a
manifest is unusual, and it points at Node tooling that has leaked into a .NET repository
(`node_modules/` with Redux packages is present but untracked).

---

## Checked and cleared

Recorded so these are not re-investigated later. Each looked like a defect and turned out not to
be one.

- **`Cli.Trace` string slice.** `src/Cli.cs:278` does `r.GetString(0)[..19]` with no length check,
  which would throw on a short timestamp. `Iso()` (`LogIngestor.cs:648-649`) always writes
  `DateTime.ToString("O")`, which is fixed-width. Confirmed against the live database:
  `MIN(LENGTH(ts))` and `MAX(LENGTH(ts))` are both **28**. Safe.

- **`Item.razor` `_history.Reverse()`.** `src/Components/Pages/Item.razor:42`. If `_history` were
  typed `List<T>`, this would bind to `List<T>.Reverse()` and mutate the resolver's internal list
  in place on every render. It is declared `IReadOnlyList<MoveRecord>` (`:59`), so overload
  resolution picks `Enumerable.Reverse`. Correct — but only because of the declared type, which
  is worth knowing if that field is ever retyped.

- **`INSERT OR IGNORE` + `changes()`.** `src/Ingest/LogIngestor.cs:466`. An ignored insert is
  still a completed `INSERT` that modified zero rows, so `changes()` returns 0 and the
  `CASE` yields `NULL`. The dedupe works as intended.

- **`Kestrel` binding.** `UseUrls("http://localhost:5730")` binds loopback only, not `0.0.0.0`.
  The UI is not exposed to the network. Correct for a local tray app.

- **`.claude/settings.local.json`.** Untracked and absent from `.gitignore`, which looked like
  an oversight. It is excluded by the user's global ignore file
  (`~/.config/git/ignore`). Not a repository issue.

- **`LedgerReplay` ordering.** `src/Resolve/LedgerReplay.cs:105` re-sorts by `Timestamp` alone
  after `LoadMoves` ordered by `ts, id`. LINQ's `OrderBy` is documented as a stable sort, so ties
  keep their `id` order and worn sightings (concatenated second) consistently follow moves at the
  same instant. Correct, though it rests on stability that is not called out in a comment.

- **`Drop` skipping `Normalize`.** `src/Ingest/LogIngestor.cs:135-147` builds the synthetic move
  and calls `Record` directly, bypassing `InventoryEventParser.Normalize` that the other two
  paths use. Traced through: the target is `InventoryRef.World`, which is `IsHolding`, is not
  `Invalid`, and can never equal the source's `Raw`, so `Normalize` would return the move
  unchanged. Behaviourally identical — just inconsistent.

---

## What I would do first

1. **H1** — one `try`/`catch`. Smallest change, removes a crash a user will hit.
2. **H2** — the build guard is there to protect a subtle failure; right now it protects nothing.
3. **M4** — start a test project. Every other item on this list gets cheaper to fix afterwards.
4. **H4 / H6** — both are data-loss paths and both are contained fixes.
5. **M5 / M6** — two lines of YAML.
