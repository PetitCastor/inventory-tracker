# Inventory Tracker

Ever stashed a rifle in a 2 SCU box three stations ago and forgotten where it went? Same.

Inventory Tracker reads Star Citizen's `Game.log` while you play and works out where your items are. No manual logging, no spreadsheet: just play.

## How it works

Star Citizen never logs a full inventory snapshot. It does log every *movement*: an item leaving one place and arriving in another. The tracker replays those movements in order, so "where are my pants?" becomes a lookup.

It runs as a tray app with a local web UI at `http://localhost:5730`. Browse by system and station, open a container to see what it holds, or follow one item's full history.

The tracker never sees your starting inventory, so it infers everything from the moves. Each result carries a confidence score and, where it applies, a note on what is uncertain (an unconfirmed move, a guessed container location, a possible duplicate). Treat the results as a well-evidenced minimum of what you own and where, not a guarantee.

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

`publish.ps1` builds a single self-contained `dist\InventoryTracker.exe` using the version in the `VERSION` file.

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
