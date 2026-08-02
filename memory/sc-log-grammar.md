---
name: sc-log-grammar
description: Star Citizen Game.log line format and the verified regex anchors for every extractable gameplay signal
metadata: 
  node_type: memory
  type: reference
  originSessionId: 638219a8-3c05-4664-be5a-0de7b5cdda12
  modified: 2026-08-01T13:25:42.217Z
---

Every Star Citizen log line: `<ISO8601-Z timestamp> [Severity] <EventTag> payload [Team_X][Subsystem]...`

Severities: `Notice` (bulk), `Error`, `Trace`, rarely `Warn*`.

**Tag census — 4.9 only** ([[sc-parse-cutoff-49]], 56 files, 349,580 lines): **355 distinct event tags**, 246,367 tagged lines. Full list in `scratchpad/tag-census-49.tsv`.

| Domain | Lines | Share |
|---|---|---|
| inventory/item | 95,899 | 38.9% |
| streaming/mesh | 45,164 | 18.3% |
| engine-noise | 41,021 | 16.7% |
| mission | 19,273 | 7.8% |
| **uncategorised** | **27,052** | **11.0%** |
| account/net | 10,282 | 4.2% |
| travel | 3,415 | 1.4% |
| ship/vehicle | 2,170 | 0.9% |
| multiplayer | 2,037 | 0.8% |
| session/exit | 54 | 0.0% |

Volume is concentrated and **inverted from interest**: 10 tags = 47% of lines, 50 tags = 88%, and the top tags are engine plumbing. The distinctive gameplay verbs (shopping kiosks, refinery, Arena Commander lobbies, hauling, master modes) live in the rare tail. **127 tags remain uncategorised**; largest are `InitializeSlotMetadata_Failed` (9,148), `SHUDEvent_OnNotification` (2,298), `CreationRefusedEntity` (1,766), `Context Establisher Blocked`/`Unblocked` (~1,720 each).

Treat this table as a map of what has been *verified*, never as a claim about what the logs contain.

Verified-working extraction anchors (counts from the 603-file corpus, see [[sc-log-corpus]]):

| Signal | Anchor |
|---|---|
| Session header | `BackupNameAttachment=" Build(N)"`, `Log started on`, `FileVersion:`, `Host CPU:` |
| Exit cause | `<SystemQuit> ... cause=N, reason=R, exitCode=E` |
| Account handle | `<Legacy login response> [CIG-net] User Login Success - Handle[X]` |
| Shard + region + server IP | `<Join PU> address[IP] port[P] shard[pub_use1b_11244073_160] locationId[N]` — 275 distinct shards; region token is field 2 of the shard string |
| Mission outcome | `<EndMission> ... MissionId[uuid] Player[X] PlayerId[N] CompletionType[C] Reason[R]` — 892 rows: 566 Complete, 293 Abandon, 26 Fail |
| Per-objective timeline | `<ObjectiveUpserted>` (mission_id, objective_id, `MISSION_OBJECTIVE_STATE_*`) + `<CMissionLogEntry::UpdateActiveObjective>` |
| Mission destinations (named) | `<GenerateLocationProperty>` — literal location names, IDs, and contract name |
| Locations visited | `<RequestLocationInventory> Player[X] requested inventory for Location[L]` |
| Ships flown | archetype prefix of the entity token in `[QuantumTravel]` / `[Vehicle]` / `<Vehicle Control Flow>` lines, e.g. `ANVL_Asgard_9947881866594` → regex `([A-Z]{3,6}(?:_[A-Za-z0-9]+)+)_\d{10,}` (331 archetypes found) |
| Quantum travel | `<Player Selected Quantum Target - Local>` (1778) → `<Quantum Drive Arrived - Arrived at Final Destination>` (1081); both carry the ship entity token |
| Ship loss proxy | `<CWallet::ProcessClaimToNextStep> New Insurance Claim Request - entitlementURN: urn:sc:entitygraph:ltp:geid:N` (854) |
| Docking / hangar | `<Command Module Ship Spawned Paired>`, `<CLandingArea::UnregisterFromExternalSystems>` (logs auto-stow) |
| Multiplayer | `<PlayerJoined>` (mission_id + player_id), `<CPartyMarkerComponent RWES>` |
| Owned fleet queries | `CEntityComponentShipListProvider::FetchOwnedShipsData` / `FetchShipData` |

## Inventory / item movement (largest domain by volume — 83,737 events in a 41-file sample)

| Signal | Anchor |
|---|---|
| Equip | `<EquipItem> Request[N] equip from Inventory[id] Class[item_class] Rank[r] Port[Body_ItemPort:...]` — 153 distinct item classes, 65 distinct ports in a 60-file sample |
| Store / unstow | `<StoreItem> ... store 'entity' [id] by 'Player' [pid] To Inventory[..] Class[..] ItemsCount[N]`, `<UnstowPendingEntities> ... finalized spawn of 'entity'` |
| Freight elevator / cargo | `<CEntityComponentFreightElevatorUIProvider::FillUnstowRequest>` — kiosk name, entity count, Location, RequestId (20,989 events / 60 files) |
| Container capacity | `<OnDragInventoryItemModifyTarget> Source[..] Capacity[..] UsedCapacity[..] MovedCapacity[..] CapacityAfterPerc[..]` — real µSCU fill levels per move |
| Loadout at spawn | `<AttachmentReceived> Player[X] Attachment[entity, class, id] Status[persistent] Port[..]` — full worn loadout each session |
| Inventory location moves | `<Update Inventory Location> Player [X] is changing location. Landing [a] -> [b]. Location [c] -> [d]` |
| Request queue / failures | `<InventoryManagementRequest>` (Type, Source/Target Inventory, Item, action), `<Add Inventory Management Move>` (Caller), `<Inventory Token Flow>` |

A single item move is **spread across ~7 lines sharing one `Request[N]`** — no line carries from *and* to. Reconstruct by joining on the request id. Request ids are **reused within a session**, so only one record per id may be open at a time. See [[sc-item-movement]] for the working extractor and the per-type endpoint semantics.

## Server meshing / streaming (missed in the first pass)

| Signal | Anchor |
|---|---|
| Zone + entity streaming | `<CContextEstablisherStepStart>` / `<ContextEstablisherTaskFinished>` (~10.5k each per 41 files) — step names + durations = loading-stall analysis |
| Streaming blocked | `<Context Establisher Blocked>` / `<Context Establisher Unblocked>` |
| Server transfer | `<Change Server Start>` / `<Change Server End>`, `<Nub ConnectTo>`, `<Channel Created/Disconnected/Destroyed>` |
| Shard lifecycle | `<SeedingProcessor::CreateShard>`, `SeedShard start`, `ActivateShard Success`, `SeedMegaMap Success` |
| Desync correction | `<RevertMoves>`, `<Too many actions>`, `<Previous index is larger than current>` |
| Other unmapped | `<CWallet::ProcessClaimToNextStep>`, `<SHUDEvent_OnNotification>`, `<SpawnCommsEntity>`, `<UI - LoadoutEditor>`, `<[ActorState] Place>`, `<[GameRules] GameRulesActionEvent_*>`, `<EOS_Logging>`, `<SubscribeToPlayerSocial>` |

**Coverage caveat.** Counts in the anchor tables above were measured across the full 603-file corpus before the [[sc-parse-cutoff-49]] hard stop; the *anchors* still hold for 4.9 but the *numbers* are all-era — re-derive before quoting. The only tested-absent signal is the killfeed — see [[sc-killfeed-removed]]. Earlier claims here that aUEC balances, framerates, and item prices are "not present anywhere" were never actually tested and have been removed.
