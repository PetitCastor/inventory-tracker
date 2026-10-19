# Development guide

For features and everyday use, see the [main README](../README.md).

## Build and test

Requires Windows and the **.NET 10 SDK**. The project targets `net10.0-windows`.

Run from the repository root. Commands use RTK as required by [AGENTS.md](../AGENTS.md).

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

Normal launch starts the tray app and local UI at [http://localhost:5730](http://localhost:5730). Console mode (`--scan`) imports logs without a UI or automatic updates.

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

`%LOCALAPPDATA%\InventoryTracker\config.json` stores the log folder, database path, UI port and wipe date. App diagnostics go to `tracker.log` in the same directory.

The selected game folder must contain `Game.log`. The tracker imports compatible `logbackups\*.log` files and the active log, then follows new events in `Game.log`.

A wipe date excludes earlier events. **Settings → Save and rebuild from logs** clears imported data and reimports with the updated settings. After editing the date in the config file, use `--scan --reset` to apply it to previously imported data.

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
