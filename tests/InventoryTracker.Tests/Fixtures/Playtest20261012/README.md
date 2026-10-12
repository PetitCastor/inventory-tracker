# Playtest of 2026-10-12

A drop-and-pickup test in the player's hangar at Keeger (Nyx), then a ship test with a Grey's
Shiv, to learn what the logs say about items left on the floor and items kept in a ship. The
player described each action in chat and checked it in game.
`Playtest/PlaytestReplay.cs` replays the file up to any moment (`PlaytestReplay.On("2026-10-12", ...)`).

## What the file is

`logbackups/` holds the day's two logs, named as their headers announce: the first session
(17:52–18:05) whole, and the second (from 18:42) cut at 19:06:00. It is trimmed the same way as the 2026-09-25 fixture: the lines the tracker parses,
plus `Join PU`, `StoreItem`, `Inventory Drop From Container` and `Inventory Mgmt Request Process`.
Object-container paths, which streaming repeats hundreds of times, are kept once per system per
minute. `Vehicle Control Flow` and `SetVehicleSpawningInformations` are kept for the ship test;
the landing area's name, which runs onto a line of its own, is not. The player's handle is replaced with `Pilot`, `Channel Disconnected` keeps only `cause`,
`reason` and `hostType`, and `Join PU` has its address, port and shard blanked.

## Timeline (UTC)

| Time | What the player did | What the game did |
|---|---|---|
| 17:53:27 | Arrive at Keeger | `Update Inventory Location` to 3058615591 |
| 17:55:03 | Drop the FSK-8 knife from Keeger's inventory, standing in the hangar | Request 12 `Drop`, source `Location:3058615591`; spawn names 268727137002 |
| 17:55:29 | Drop the Arclight from the backpack | Request 13 `Drop`, source the backpack 726852737766; spawn names 862186749926 |
| 17:56:02 | Log out, then join an EU server | `Channel Disconnected` cause 30016 |
| 17:57:04 | Back at Keeger | Body burst, `Update Inventory Location` to 3058615591 again |
| 17:57:18 | Pick the Arclight up into the hand | Only `AttachmentReceived` on `weapon_attach_hand_right` |
| 17:57:23 | Pick the knife up | `AttachmentReceived` on the hand, then on `utility_attach_2`: the game holstered it |
| 17:57:28 | Store the Arclight in the backpack | Request 24 `Store`, naming 862186749926 |

### Second session: the ship

| Time | What the player did | What the game did |
|---|---|---|
| 18:43:27 | Retrieve the Shiv to the hangar | `SetVehicleSpawningInformations`, VehicleEntityId 876737712932 |
| 18:45:18 | Drop the Arclight on the ship's floor, in the hangar | Request 14 `Drop` from the backpack; Keeger's location |
| 18:45:44 | Pick it up into the backpack | Request 18 `Store`, naming 862186749926 |
| 18:45:50 | Open the ship's lockers | Token Flow names `PersonalStorage_GLSN_Shiv-001` 876737713106 and `-000` 876737713105 |
| 18:45:56 | Arclight into the locker | Request 24 `Move` to 876737713105 |
| 18:46:23 | Arclight back into the backpack | Request 25 `Move` from 876737713105 |
| 18:48:00 | Leave the pilot seat, just outside the hangar | `releasing control token for 'GLSN_Shiv_876737712932'`; no location change |
| 18:48:13–24 | Drop the ParaMed from the holster | Three `Inventory Drop From Container` naming the Arclight's geid, no `Drop` request |
| 18:48:50 | Pick the ParaMed up into the backpack | Request 29 `Store`, naming 735739878137 |
| 18:49:32 | ParaMed into the locker, in flight | Request 35 `Move` to 876737713105 |
| 18:53:57 | ParaMed from the locker to the holster | Request 36 `EquipItem` |
| 18:56:13 | Drop the ParaMed from the holster | Only an `INVALID` to `INVALID` drag; no line names it |
| 18:57:47 | Pick it up onto the holster | `AttachmentReceived` on `wep_sidearm` |
| 19:02:55 | Arclight into the locker | Request 45 `Move` to 876737713105 |
| 19:03–19:04 | Store the ship | No line of its own; the vehicle count at Keeger goes from 1 to 2 |
| 19:04:21 | Retrieve it | `SetVehicleSpawningInformations`, the same 876737712932 |
| 19:05:31 | Arclight out of the locker into the backpack | Request 51 `Move` from 876737713105 |

## What it showed

- The hangar kept both items across the logout and the server switch.
- The hangar has no location id of its own: everything happened under Keeger's 3058615591, and
  no line tells the hangar floor from the rest of the station.
- A ship's lockers keep their geids in the hangar, in flight and across a store and a retrieve,
  and so do their contents. Nothing ties a locker to the ship's geid except the class in its name.
- A drop out of a holster writes no `Drop` request and names nothing reliably.
- Picking an item up off the floor writes no move line. The attachment of the same geid to a
  hand or holster port is the only record.
