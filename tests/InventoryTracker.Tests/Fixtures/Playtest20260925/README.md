# Playtest of 2026-09-25

The logs of a live playtest. The player described each action in chat, and each step was
checked three ways: against `Game.log`, against the tracker's holdings, and in game by the
player. `Playtest/PlaytestReplay.cs` replays these files up to any moment of the day.

## What the files are

`logbackups/` holds the day's 13 log files, in the names the game gives rotated backups. The
evening file was copied while it was still the live `Game.log` and carries the backup name its
header announces.

Each file is trimmed to the lines the tracker parses, plus a few the fix plan needs
(`Channel Disconnected`, `Join PU`, `EquipItem`, `StoreItem`, `Inventory Drop From Container`,
`Inventory Mgmt Request Process`, the corpse lines and the medbed notification). The player's
handle is replaced with `Pilot`. `Channel Disconnected` keeps only `cause`, `reason` and
`hostType`, and `Join PU` has its address, port and shard blanked. No other player appears in
the kept lines.

## Timeline (UTC)

| Time | What the player did | What the game did |
|---|---|---|
| 10:49–10:52 | Launch in the Mole, after a crash | Placeholder set, then the real loadout |
| 10:54:23 | Suicide in space | 24 corpse lines; the loadout re-issued under new geids |
| 11:03–11:55 | Eight launches, most ending in a crash | Placeholder + real set on each |
| 15:34:49 | Launch at Area18 | Placeholder set 15:35:06, real set 15:35:47 |
| 15:44:48 | Aril helmet from Area18 into the backpack | Request 28, never processed |
| 15:46:26, 15:46:29 | Two Defiance helmets into the backpack | Requests 29, 30, never processed |
| 15:50:20 | Aril back to Area18 | Request 31, never processed |
| 15:53:23 | Switch to an EU server | `Channel Disconnected` cause 30016; requests 28–31 lost. The player saw all three helmets back at Area18 |
| 15:57:37 | Equip the Aril straight from Area18 | Request 53 stores the qrt helmet, 54 equips the Aril |
| 15:59:19 | Aril from the head into the backpack | Request 55 |
| 16:00:12 | Drop a Defiance from Area18 onto the floor | Request 56 `Drop` |
| 16:01:33 | Pick it up straight into Area18 | Request 57 `Store`, same geid |
| 19:57:11 | Relaunch | Placeholder + real set |
| 20:00:05 | Swap the behr sniper for the A03 | Request 11 stores, 12 equips |
| 20:02:35 | Swap the qrt backpack for the rsi backpack | Requests 13–14 store both guns, 15 the backpack, 16 equips the rsi backpack |
| 20:06 | Backpack swapped back, helmet re-equipped, Aril to Area18, cds helmet into the backpack | Requests 25–30 |
| 20:08:34 | Death at Area18, medbed respawn | One body burst re-sends the same geids; the backpack keeps the cds helmet |
| 20:09 | cds helmet on and back into the backpack | Requests 41–44 |

## Ground truth at the end (20:10)

- **On the character:** hdtc undersuit …829; qrt arms …842, core …830, legs …843, helmet …844
  with its visor …845; qrt backpack …831. No weapon, no pistol, no medpen.
- **In the backpack …831:** the cds undersuit helmet 310168150377, with its visor and necksock.
- **At Area18:** the A03 735739878129 with its magazine and optic; the behr rifle …837 and the
  behr sniper …832, each with its attachments; the Aril 726852737760; two Defiance helmets,
  one of them 687179504351; the rsi backpack 723713568285, empty.

Geids starting with `2000` belong to the placeholder set the game attaches on every login
before the real loadout arrives. They are never the player's items.
