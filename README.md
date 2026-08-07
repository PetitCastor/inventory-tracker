# InventoryTracker

Ever stashed a rifle in a 2 SCU box three stations ago and had no idea where it went? Same.

InventoryTracker watches Star Citizen's `Game.log` while you play and reconstructs where your stuff actually is by replaying every move it sees — drags, drops, stows, container transfers, the works. No manual logging, no spreadsheet. Just play, and the tracker builds the picture from what the game already writes to disk.

## How it works

Star Citizen never hands you a clean inventory snapshot — there's no "here's everything you own" event. What it does log, constantly, is *movement*: an item leaving one place and landing in another. InventoryTracker reads that stream and replays it forward, so "where's my pants" becomes a lookup instead of a memory test.

It runs as a small tray app with a local web UI (`http://localhost:5730`) — browse by system and station, drill into a container's contents, or trace one item's full history.

Because it's built entirely from inferred deltas over an unknown starting state, every result comes with a confidence score and, where relevant, a caveat explaining exactly what's uncertain about it (an unconfirmed move, a guessed container location, a possible duplicate). It's a well-evidenced lower bound on what you own and where — not a guarantee.

## Running it

```
InventoryTracker.exe                     tray app + UI at http://localhost:5730
InventoryTracker.exe --scan --holdings   console mode, no UI
```

First launch walks you through picking your Star Citizen log folder and an inception date (everything logged before that is ignored).
