# Inventory Tracker

Ever stashed a rifle in a 2 SCU box three stations ago and had no idea where it went? Same.

Inventory Tracker watches Star Citizen's `Game.log` while you play and reconstructs where your stuff actually is by replaying every move it sees — drags, drops, stows, container transfers, the works. No manual logging, no spreadsheet. Just play, and the tracker builds the picture from what the game already writes to disk.

## How it works

Star Citizen never hands you a clean inventory snapshot — there's no "here's everything you own" event. What it does log, constantly, is *movement*: an item leaving one place and landing in another. Inventory Tracker reads that stream and replays it forward, so "where's my pants" becomes a lookup instead of a memory test.

It runs as a small tray app with a local web UI (`http://localhost:5730`) — browse by system and station, drill into a container's contents, or trace one item's full history.

Because it's built entirely from inferred deltas over an unknown starting state, every result comes with a confidence score and, where relevant, a caveat explaining exactly what's uncertain about it (an unconfirmed move, a guessed container location, a possible duplicate). It's a well-evidenced lower bound on what you own and where — not a guarantee.

## Running it

```
InventoryTracker.exe                     tray app + UI at http://localhost:5730
InventoryTracker.exe --scan --holdings   console mode, no UI
```

First launch walks you through picking your Star Citizen log folder and an inception date (everything logged before that is ignored). The tracker probes the usual install locations, so the folder is usually already filled in.

## Building it

```
dotnet build InventoryTracker.slnx
dotnet test InventoryTracker.slnx
.\publish.ps1
```

`publish.ps1` produces a single self-contained `dist\InventoryTracker.exe` with no .NET prerequisite, using the version in the `VERSION` file. Every merge to `main` cuts a new GitHub Release regardless of whether that PR touched `VERSION`: CI releases whatever's in the file if it's ahead of the latest published release (a deliberate minor/major bump), otherwise it patch-bumps past the latest release on its own.

## Layout

| Path | What's in it |
|---|---|
| `src/Ingest` | Reading `Game.log` and turning lines into typed events |
| `src/Resolve` | Replaying those events into "what is sitting where" |
| `src/Naming` | Class names → human names, location ids → places |
| `src/Components` | The Blazor UI |
| `src/App` | Tray host, config, shared state |
| `tests/` | Parser, ledger and reader tests |
| `docs/domain` | Reverse-engineering notes on the log format |
