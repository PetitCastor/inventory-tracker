# Inventory Tracker

Find your items in **Star Citizen**. Inventory Tracker reads the game's logs to track observed item movements and estimate where your items are.

**[Download InventoryTracker.exe](https://github.com/PetitCastor/inventory-tracker/releases/latest/download/InventoryTracker.exe)** — Windows, single file, no installer or .NET installation required.

## Features

| View | What you can do |
|---|---|
| **Locations** | Search items, view quantities, filter by system and station, and browse containers and nested contents. Name or confirm unresolved stations. |
| **Ships** | View inferred ship-locker contents and, when available, the ship's last retrieval location and time. |
| **Dropped** | Find recorded ground drops, grouped by location. |
| **Item details** | See an item's estimated location, last move, confidence and movement history. |
| **Settings** | Change the log folder or wipe date, rebuild the inventory, refresh names from the wiki, check for updates and inspect log-reading diagnostics. |

## Getting started

1. Download the executable, put it in a writable folder and run it.
2. Select the Star Citizen folder containing **`Game.log`**, usually **`LIVE`** or **`PTU`**. Common install locations are detected automatically.
3. Optionally set a persistence wipe date to ignore earlier events. Leave it blank to use all available compatible history.
4. Open [http://localhost:5730](http://localhost:5730). Keep the tracker running while you play: it imports available compatible logs, then follows new events.

The app runs in the Windows system tray. Double-click its icon to open the interface. Right-click for **Rescan logs now**, **Refresh names from wiki** or **Exit**.

To change the log folder or wipe date later, open **Settings** and use **Save and rebuild from logs**.

## Tracking limits

- **No starting inventory.** Items appear only after a usable log event identifies them. Missing logs or a wipe cutoff can leave items out.
- **Locations are estimates.** The game does not log every movement. Positions may be stale, inferred or unconfirmed. Confidence reflects available evidence, not a guarantee that an item is still there.
- **Ships are grouped by model.** Lockers from multiple ships of the same model may appear together; the logs cannot distinguish individual ships.
- **Ground drops are search leads.** Items can despawn without a log event. Drops from holsters or other equipment slots do not appear in **Dropped**.

See the [replay reliability study](docs/reliability/README.md) for measured limits.

## Updates and troubleshooting

The tray app checks for updates at startup, installs a newer release if available and restarts. Wiki name refreshes and GitHub updates require an internet connection.

If tracking stops, check the selected log folder and last-read time in **Settings**. App logs and configuration are in `%LOCALAPPDATA%\InventoryTracker` (`tracker.log` and `config.json`).

## Technical documentation

See the [development guide](docs/development.md) for builds, console commands, configuration, architecture and releases.
