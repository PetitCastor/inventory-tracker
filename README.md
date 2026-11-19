# Inventory Tracker

Find your items in **Star Citizen**. Inventory Tracker reads the game's logs to track observed item movements and estimate where your items are.

**[Download InventoryTracker.exe](https://github.com/PetitCastor/inventory-tracker/releases/latest/download/InventoryTracker.exe)** — Windows, single file, no installer or .NET installation required.

## Features

| View | What you can do |
|---|---|
| **Locations** | Search items, view quantities, filter by system and station, and browse containers and nested contents. Name or confirm unresolved stations. After a character reset, see which items moved to your new home location. |
| **Ships** | View inferred ship-locker contents and, when available, the ship's last retrieval location and time. |
| **Dropped** | Find recorded ground drops, grouped by location. |
| **Item details** | See an item's estimated location, last move, confidence and movement history. |
| **Settings** | Change the log folder or wipe date, rebuild the inventory, refresh names from the wiki, check for updates, read recent changes, pick a light, dark or system theme and inspect log-reading diagnostics. |

## Getting started

1. Download the executable, put it in a writable folder and run it.
2. Select the Star Citizen folder containing **`Game.log`**, usually **`LIVE`** or **`PTU`**. Common install locations are detected automatically.
3. Optionally set a persistence wipe date to ignore earlier events. Leave it blank to use all available compatible history.
4. The tracker opens in its own window. Keep it running while you play: it imports available compatible logs, then follows new events.

Closing the window keeps the tracker running in the Windows system tray. Double-click the tray icon to reopen the window. Right-click for **Open in browser**, **Rescan logs now**, **Refresh names from wiki** or **Exit**. The browser shows the same interface at [http://localhost:5730](http://localhost:5730), for a second screen for example.

The window needs the Microsoft Edge WebView2 Runtime, which Windows 11 and up-to-date Windows 10 include. Without it the interface opens in your browser instead.

The header shows the Star Citizen version of the newest log read, taken from the RSI Launcher's logs; it falls back to the build number when the launcher no longer names that build.

To change the log folder or wipe date later, open **Settings** and use **Save and rebuild from logs**.

## Tracking limits

- **No starting inventory.** Items appear only after a usable log event identifies them. Missing logs or a wipe cutoff can leave items out.
- **Locations are estimates.** The game does not log every movement. Positions may be stale, inferred or unconfirmed. Confidence reflects available evidence, not a guarantee that an item is still there.
- **Character resets are followed, other patch moves are not.** When a patch forces a character reset, the game moves your stored items to the home location you pick without logging it. The tracker detects the reset, moves the items it knows about there and tells you once in a popup. Spawning somewhere new without a reset moves nothing.
- **Very large multi-item moves can be cut short.** Game.log truncates long lines, so moving about 25 items or more at once can leave some of them unnamed. A warning on **Locations** reports log lines that could not be fully read.
- **Ships are grouped by model.** Lockers from multiple ships of the same model may appear together; the logs cannot distinguish individual ships.
- **Ground drops are search leads.** Items can despawn without a log event. Drops from holsters or other equipment slots do not appear in **Dropped**.

See the [replay reliability study](docs/reliability/README.md) for measured limits.

## Updates and troubleshooting

The tray app checks for updates at startup, installs a newer release if available and restarts. After an update, a **What's new** popup lists the changes since the version you last used. Wiki name refreshes, GitHub updates and release notes require an internet connection.

If tracking stops, check the selected log folder and last-read time in **Settings**. App logs and configuration are in `%LOCALAPPDATA%\InventoryTracker` (`tracker.log` and `config.json`).

## Support and contact

- **Bug or wrong item location:** [open an issue](https://github.com/PetitCastor/inventory-tracker/issues/new/choose). Log excerpts help a lot.
- **Question or idea:** start a thread in [Discussions](https://github.com/PetitCastor/inventory-tracker/discussions).
- **Security problem:** report it privately, as described in [SECURITY.md](SECURITY.md).

Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Technical documentation

See the [development guide](docs/development.md) for builds, console commands, configuration, architecture and releases.

## License

[MIT](LICENSE)

Inventory Tracker is an unofficial fan project, not affiliated with or endorsed by Cloud Imperium Games. Star Citizen is a trademark of Cloud Imperium Rights LLC.
