---
name: sc-item-movement
description: How to extract a from->to item movement ledger from SC logs, including the two logging eras and per-type endpoint semantics
metadata:
  type: reference
---

A full item-movement ledger **is** extractable. Under the [[sc-parse-cutoff-49]] hard stop (4.9 only, build >= 12232306): **1,639 movements over 56 files, 2026-07-16 → 2026-08-01, 90% carrying both endpoints**. Extractor: `scratchpad/extract-item-moves.ps1`, 0.2 min, output `item-moves.tsv`.

**Join model.** No single line carries both endpoints. Open a record on `<Add Inventory Management Move>` *or* `<InventoryManagementRequest> Attempting queuing`, enrich from `<EquipItem>`, `<StoreItem>`, `Queued Request`, `<Player Inventory Request Complete>`, `<Update Inventory>`, `<UnstowPendingEntities>`, and close on `<Inventory Request Completed>`. Request ids are reused within a session — keep at most one open record per id and flush on reopen.

**One request can move many items.** A batch move writes a *list*: `ItemClass[[a] [b] [c] ]`. Any field reader that ends a value at the first `]` silently truncates it to `[a` — first item only, stray leading bracket, other items gone from the ledger entirely. 41 such lines in the 4.9 set; fixing it recovered **224 movement rows (1,645 → 1,869)** and dissolved phantom `[classname` instances that had split one item's history in two. Fan out one row per class, and let only the class matching `<class>_<id>` claim the request's single spelled-out `StoredEntity` — otherwise every item in the batch inherits one id and collapses into a single wrongly named instance.

**Endpoint semantics differ per `Type[]` — this is the main trap.** Field names lie:

| Type | From | To | Note |
|---|---|---|---|
| `Interaction` | source inventory | body `Port[...]` | equipping; with `action[Carry]` on the Queued line, picking into the hand to eat, drink or hand in |
| `Store` | Hands (`LocallyDetached[Yes]`) or body port | container/location | **`SourceInventory` on the Add-Move line is really the DESTINATION**; `TargetInventory` is `INVALID` |
| `Move` | source inventory | target inventory | drag between containers |
| `Drop` | source inventory | world | |
| `Stack`/`Split`/`StackAll`/`Sort`/`AmmoRepool` | one inventory | same inventory | reorganise; from == to is correct, not a bug |
| `Swap` | container A | container B | |
| `QueryInventory`/`QueryOrCreate`/`OpenNestedInventory` | — | — | reads, not movements |

**Two logging eras.** `<Add Inventory Management Move>`, `<Inventory Request Completed>` and `<Player Inventory Request Complete>` appear only from **build 12232306 (2026-07-16)** onward — i.e. only in 4.9, the era we parse. Earlier builds do log movements via `Attempting queuing` + `Queued Request`, but omit the completion line and rarely populate the Hands/port side, so their rows are mostly single-endpoint. That legacy path is implemented and works (20,818 movements back to 2026-03-05 with `-IncludeLegacy`) but is **deliberately off** per [[sc-parse-cutoff-49]] — excluding it is the rule, not a bug to fix. `Result = no_completion_logged` is a legacy-only artefact meaning missing telemetry, never a failed move.

**Game updates move items without logging it.** Build 12519617 was first played on 2026-08-27. In that session, 13 items the ledger had at Lorville, at Levski or equipped were all moved out of Area18, the station where the player spawned after the update. No line records the server relocating them. Within one build, every named move out of a place agreed with the ledger, up to 21 days later. Treat a change of build, not age, as what makes a placement stale. That update also put the player somewhere new: they had logged out at Stanton Gateway. The next two updates (12572603, 12660092) left the player where they had logged out, and no item was checked across them. So the tracker moves belongings stored at a station or on the player to the new spawn only when an update changed it. The [reliability study](../reliability/README.md) checks, per update, whether they were found there.

**Places in open space are named only by quantum travel.** `<Player Selected Quantum Target - Local> … Player has selected point <code> as their destination`, then `<Quantum Drive Arrived - Arrived at Final Destination>`, then `<Update Inventory Location>` into the place around that point, within about 3 seconds. A ship parked at a mining site (`ab_mine_stanton3_sml_003`) gets no other name: no station inventory, no route plotted from it. Mission beacons (`MISSION_QT_…`) are one-off points. A system's open space is entered on the way to everything in it, so it collects arrivals for many points.

**Name resolution.** Container ids (`<id>:Container:0`) resolve to readable names by harvesting every `<name>_<entityid>` token seen anywhere in `[Inventory]` lines — 15,678 entries corpus-wide. This is what turns `681562156430` into `Carryable_TBO_InventoryContainer_2SCU`. `Lootable_Container_*` sources make looting measurable (2,443 loot pulls). Locations do **not** work this way — see [[sc-location-ids]].

**Third era.** From build 12519617 (late Aug 2026) the grammar changed again, in exactly two spots, with every line body otherwise byte-identical to the second era above: the Queued tag renamed `<InventoryManagementRequest>` to `<Inventory Mgmt Request Queued>`, and the Add-Move line's `New request[` became `New Request[` (capital R). The parser dispatches both Queued tags to the same handler and accepts either casing of the request token; everything past those two tokens stays case-sensitive. Any line that still looks request-shaped — `Queued Request[N]` or `New request[N]`/`New Request[N]`, on a tag we dispatch on or one we've never seen — but that none of the parser's regexes can actually read now emits `UnrecognisedMove` instead of silently vanishing, so a fourth grammar change shows up as a counter going up (`Unrecognised moves` in the CLI stats, `IngestHealth.UnrecognisedMoveLines` for the UI) rather than as items quietly disappearing from the tracked inventory.

**Geids are stable across sessions.** Measured on the corpus: 282 of 388 worn geids recur across more than one log file, and 15 of 16 retrievals of a stored item in a later session spawn the same geid it was stored under. An item's identity survives a game restart, so a geid learned in one session is safe to trust in the next.

See [[sc-log-grammar]] for line format, [[sc-log-corpus]] for the corpus shape.
