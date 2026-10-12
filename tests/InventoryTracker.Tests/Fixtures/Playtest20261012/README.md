# Playtest of 2026-10-12

A drop-and-pickup test in the player's hangar at Keeger (Nyx), to learn what the logs say about
items left on the floor. The player described each action in chat and checked it in game.
`Playtest/PlaytestReplay.cs` replays the file up to any moment (`PlaytestReplay.On("2026-10-12", ...)`).

## What the file is

`logbackups/` holds the one log of the session, cut at 17:58:00 and named as its header
announces. It is trimmed the same way as the 2026-09-25 fixture: the lines the tracker parses,
plus `Join PU`, `StoreItem`, `Inventory Drop From Container` and `Inventory Mgmt Request Process`.
Object-container paths, which streaming repeats hundreds of times, are kept once per system per
minute. The player's handle is replaced with `Pilot`, `Channel Disconnected` keeps only `cause`,
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

## What it showed

- The hangar kept both items across the logout and the server switch.
- The hangar has no location id of its own: everything happened under Keeger's 3058615591, and
  no line tells the hangar floor from the rest of the station.
- Picking an item up off the floor writes no move line. The attachment of the same geid to a
  hand or holster port is the only record.
