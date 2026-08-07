---
name: sc-location-ids
description: Location endpoints are player-id-led and numeric; how to get the real place name out of them and why the leading id is not identity
metadata:
  type: reference
---

An inventory endpoint at a station is written `<playerid>:Location:<locationid>` — e.g. `204821708183:Location:3723364946`. **The leading id is the owning player, not the place.** It is identical on every location row in the corpus, so using it as the endpoint's identity makes every location in the game look like one endpoint. Identity and naming both come from the **sub-id**.

`3723364946` = **Nyx / Levski**. `4005457614` = **Stanton1_Lorville**.

**Two disjoint location namespaces.** The client never writes the number and the name on the same line:

| Namespace | Where it appears |
|---|---|
| numeric location id | `:Location:<id>` endpoints, `<Update Inventory Location>`, `<OnRequestFetchVehicles>` |
| named place | `<RequestLocationInventory> ... Location[Nyx_Levski]` |

**The join.** Arriving somewhere emits the id first and the name ~1 ms later:

```
00:11:47.230 <Update Inventory Location> Player [X] is changing location. Landing [141810852] -> [3723364946]. Location [2627827560] -> [3723364946].
00:11:47.231 <RequestLocationInventory> Player[X] requested inventory for Location[Nyx_Levski]
```

So: track the current id off `<Update Inventory Location>`, and let the next `<RequestLocationInventory>` name it. The id must **persist**, not be consumed — reopening a panel where you already stand emits a visit line with no fresh arrival line. First spelling wins; ids are stable across sessions and shards, so one sighting names every later reference corpus-wide.

**Gotcha:** the change line spaces its field names (`Location [a] -> [b]`, `Player [X]`), so the ordinary `name[value]` field reader cannot read it — it requires `name[` with no space. Needs its own regex: `\bLocation \[(\d+)\] -> \[(\d+)\]`.

Without the join, endpoints fall back to a synthetic `Location_<subid>` placeholder, which reads like an entity name but is not one — that placeholder is what makes a location look like it might be the same thing as a container. It is not: containers are `<entityid>:Container:0` and resolve through [[sc-item-movement]]'s name dictionary, a completely separate key space. Never resolve a location id against it.
