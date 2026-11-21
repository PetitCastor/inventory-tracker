# Development guide

For features and everyday use, see the [main README](../README.md).

## Build and test

Requires Windows and the **.NET 11 SDK** (11.0.100 or a later 11.0.x; `global.json` pins it so a preview SDK is never picked). The project targets `net11.0-windows`.

Run from the repository root. Commands use RTK as required by [CLAUDE.md](../CLAUDE.md).

```powershell
rtk dotnet build InventoryTracker.slnx
rtk dotnet test InventoryTracker.slnx
```

## Publish

```powershell
rtk powershell -NoProfile -File .\publish.ps1
```

Produces `dist\InventoryTracker.exe`: Release, `win-x64`, self-contained, single file with native SQLite dependencies. The version comes from `VERSION`.

| Option | Effect |
|---|---|
| `-OutputPath` | Publish to a new or empty directory outside `dist`, leaving `dist` and running processes alone. |
| `-Runtime` | Override the target runtime identifier. |
| `-FrameworkDependent` | Require installed .NET, ASP.NET Core and Windows Desktop runtimes. |

Isolated publish:

```powershell
rtk powershell -NoProfile -File .\publish.ps1 -OutputPath .\publish-review
```

## Run and inspect

Normal launch starts the tray app, the local UI at [http://localhost:5730](http://localhost:5730) and the app window, a WebView2 view of that UI (`src/App/AppWindow.cs`). Closing the window hides it to the tray. A second launch brings the running window forward. When WebView2 cannot start, the UI opens in the default browser. The window's WebView2 profile is in `%LOCALAPPDATA%\InventoryTracker\WebView2`. Console mode (`--scan`) imports logs without a UI or automatic updates.

```powershell
rtk .\dist\InventoryTracker.exe
rtk .\dist\InventoryTracker.exe --scan --holdings
rtk .\dist\InventoryTracker.exe --help
```

| Option | Purpose |
|---|---|
| `--scan` | Run in console mode. |
| `--holdings` | Print reconstructed item positions. |
| `--find <text>` | Filter items by technical class name. |
| `--trace <geid>` | Print an item's movement history. |
| `--reset` | Clear imported data before reimporting; preserve wiki name caches. |
| `--refresh-names` | Refresh names from the wiki. |
| `--names` | Report name-catalogue coverage for observed item classes, including unresolved classes. |
| `--help`, `-h` | Show command-line help. |

Rebuild from available logs using the current configuration:

```powershell
rtk .\dist\InventoryTracker.exe --scan --reset --holdings
```

Resetting can recover only events still present in available logs.

## Configuration and ingestion

`%LOCALAPPDATA%\InventoryTracker\config.json` stores the log folder, database path, UI port, wipe date, theme (`Theme`: `System`, `Light` or `Dark`; absent means `System`), whether the latest character-reset popup was closed (`ResetNoticeDismissed`) and the last version whose release notes were shown (`LastSeenVersion`). The last two are kept outside the database so a rebuild does not show them again. App diagnostics go to `tracker.log` in the same directory.

The selected game folder must contain `Game.log`. The tracker imports compatible `logbackups\*.log` files and the active log, then follows new events in `Game.log`.

A wipe date excludes earlier events. When the tracker detects a character reset later than the wipe date, it moves the date to the reset's UTC day in config.json without re-reading: the replay has already cleared everything at the reset. **Settings → Save and rebuild from logs** clears imported data and reimports with the updated settings. After editing the date in the config file, use `--scan --reset` to apply it to previously imported data.

The game version in the header is read from the RSI Launcher logs (`%APPDATA%\rsilauncher\logs`).

Name refreshes use `api.star-citizen.wiki`; app updates use GitHub Releases.

## Repository layout

| Path | Purpose |
|---|---|
| `src/Ingest` | Parse game logs into typed events. |
| `src/Resolve` | Replay events to reconstruct item positions. |
| `src/Naming` | Resolve class names and location identifiers to readable names. |
| `src/Components` | Blazor UI. |
| `src/App` | Tray host, configuration and shared state. |
| `src/Data` | Database access and storage. |
| `src/Cli.cs` | Console options and execution. |
| `tests/` | Parser, ledger and reader tests; reliability tests under `InventoryTracker.Tests/Reliability`. |
| `docs/domain` | Reverse-engineering notes on log formats. |
| `docs/reliability` | Replay reliability measurements and improvement plans. |

See the [log-format notes](domain/README.md) and [reliability study](reliability/README.md). Confidence scores are heuristic; logs provide neither a complete starting inventory nor independent proof of current item locations.

## Releases

The [CI workflow](../.github/workflows/ci.yml) creates a GitHub Release after successful builds and tests on pushes to `main`. If `VERSION` is ahead of the latest release, CI uses it; otherwise, it increments the patch version.

The tray app checks for updates at startup and restarts after installing a newer release. Console mode never updates automatically.

Each release's body is the subject of the squash commit that shipped it, which is the merged PR's title. The **What's new** popup and **Settings → Updates** read those bodies from the GitHub API and show `feat:`, `perf:` and `fix:` lines as New, Improved and Fixed. Other types (`ci:`, `docs:`, `test:`, dependency bumps) stay out of the app. A PR title is therefore also the release note, so write `feat:` and `fix:` titles for players.
