# Inventory Tracker

Ever stashed a rifle in a 2 SCU box three stations ago and forgotten where it went? Same.

Inventory Tracker reads Star Citizen's `Game.log` while you play and works out where your items are. No manual logging, no spreadsheet: just play.

## How it works

Star Citizen does not log a full inventory snapshot, and its logs do not capture every item movement. The tracker estimates item locations from observed log events and the latest plausible position it can derive from them, so "where are my pants?" becomes a useful lookup.

It runs as a tray app with a local web UI at `http://localhost:5730`. Browse your locations by system and station and open a container to see what it holds, see what is in each ship's lockers and what you dropped on the floor, or follow one item's full history.

The tracker cannot see your starting inventory, so items without observed events may be missing and inferred locations can be stale or uncertain. Each result carries a confidence score and, where it applies, a note on uncertainty (an unconfirmed move, a guessed container location, a possible duplicate). The score describes support in the available evidence; it is not a guarantee of truth. See the [replay reliability study](docs/reliability/README.md) for measured limits.

## Running it

Download **[InventoryTracker.exe](https://github.com/PetitCastor/inventory-tracker/releases/latest/download/InventoryTracker.exe)**. It is a single self-contained file: no installer, no .NET needed. Put it in any writable folder and run it.

The tray app checks for a newer release each time it starts, and updates itself and restarts if it finds one. Console mode (`--scan`) never updates.

```
InventoryTracker.exe                     tray app + UI at http://localhost:5730
InventoryTracker.exe --scan --holdings   console mode, no UI
```

On first launch you pick your Star Citizen log folder. It is usually pre-filled, since the tracker checks the common install locations.

The tracker learns about an item only when a log line moves it, so by default it reads your whole history. You can set a wipe date if Star Citizen wiped persistence. Everything logged before that date is then ignored, including items that have not moved since.

While the app runs, its own log goes to `tracker.log`, next to `config.json` in `%LOCALAPPDATA%\InventoryTracker`. Settings shows when the game's log was last read.

## Building it

```
dotnet build InventoryTracker.slnx
dotnet test InventoryTracker.slnx
.\publish.ps1
```

`publish.ps1` builds a single self-contained `dist\InventoryTracker.exe` using the version in the `VERSION` file. For an isolated publish, pass `-OutputPath` with a new or empty directory; that mode leaves `dist\` and running processes alone.

Every merge to `main` cuts a GitHub Release. If `VERSION` is ahead of the latest release (a deliberate minor or major bump), CI releases that version. Otherwise it bumps the patch number by itself.

## Layout

| Path | What's in it |
|---|---|
| `src/Ingest` | Reads `Game.log` and turns lines into typed events |
| `src/Resolve` | Replays events into "what is sitting where" |
| `src/Naming` | Class names to human names, location ids to places |
| `src/Components` | The Blazor UI |
| `src/App` | Tray host, config, shared state |
| `tests/` | Parser, ledger and reader tests, plus the replay reliability study (`tests/InventoryTracker.Tests/Reliability`) |
| `docs/domain` | Reverse-engineering notes on the log format |
| `docs/reliability` | How trustworthy the replay is, measured per pipeline stage, and the plan to improve it |
