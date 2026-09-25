# Replay reliability study

The tracker never sees an inventory. It sees a stream of move requests and replays them,
so every holding it shows is an inference. This study measures how good that inference
is. It replays a corpus of `Game.log` files through the real pipeline (parser, ingestor,
ledger, resolver) and scores each stage. Changes to the pipeline are judged by these
numbers rather than by eye.

Code: [`tests/InventoryTracker.Tests/Reliability/`](../../tests/InventoryTracker.Tests/Reliability/).

## Running it

The committed **sample** corpus runs with every `dotnet test`, CI included. It contains
inventory lines from three real sessions, with the player name replaced, and covers both
logging formats (before and after build 12519617).

```bash
dotnet test tests/InventoryTracker.Tests --filter "FullyQualifiedName~Reliability"
```

To study your own logs, point the study at a log directory (the one holding `Game.log` and
`logbackups/`) or at a single `.log` file:

```powershell
$env:INVENTORYTRACKER_CORPUS = "E:\Games\StarCitizen\LIVE"      # or a path to one Game.log
$env:INVENTORYTRACKER_CORPUS_NAME = "local-live"                # optional, names the report
dotnet test tests/InventoryTracker.Tests --filter "FullyQualifiedName~Local_corpus"
```

Each run writes `artifacts/reliability/<corpus>.md` (the report) and `<corpus>.json` (the raw
numbers). `artifacts/` is git-ignored because a local corpus's report quotes your log lines.

### The ratchet

`Reliability/baseline.json` records each corpus's metrics. A run fails when:

- **an invariant breaks**, on any corpus: a move request line the parser cannot read, or
  live tailing that disagrees with a single pass;
- **a metric gets worse than its baseline**, by more than 0.2 points, on a corpus whose
  files have not changed since the baseline was recorded.

An improvement does not move the baseline by itself. Once a change makes a metric better,
re-record the baseline so the gain becomes the new floor:

```powershell
$env:INVENTORYTRACKER_UPDATE_BASELINE = "1"
dotnet test tests/InventoryTracker.Tests --filter "FullyQualifiedName~Sample_corpus"
```

Commit the updated `baseline.json` with the change that earned it.

## What is measured

Each stage has its own metrics, so a regression points at the stage that caused it.

| Stage | Metric | Target | Meaning |
|---|---|---:|---|
| Capture | `capture.request_rate` | 98 % | Relocating requests (Add-move lines of type Move, Store, Interaction, Drop, …) that became at least one move row. Counted from the line's shape alone, independently of the parser. |
| | `capture.unrecognised_lines` | **0** (invariant) | Request-shaped lines the parser could not read (`IngestStats.UnrecognisedMoves`). Non-zero means the game changed its format. |
| Enrichment | `enrich.geid_coverage` | 85 % | Moves that name the exact entity rather than just a class. |
| | `enrich.confirmed_rate` | 95 % | Moves carrying a `succeed` completion. An unconfirmed placement is scored ×0.5. |
| Ledger | `ledger.accounted_rate` | 95 % | Class-level units the ledger can account for: found at the source, found on the player, or the class's first appearance. |
| | `ledger.duplicate_risk_rate` | 2 % | Class-level units credited without a source while the same class was still recorded elsewhere. One of the two is wrong. |
| Holdings | `holdings.high_share` | 80 % | Rows rated High confidence, the ones the user can trust outright. |
| | `holdings.low_share` | 5 % | Rows rated Low. |
| | `holdings.mean_score` | 0.85 | Mean resolver score. |
| | `holdings.inferred_share` | 5 % | Rows that may be counted twice: an arrival with no source while the class was recorded elsewhere. |
| | `holdings.first_seen_share` | 15 % | Rows whose history starts with the move that revealed them: loot, purchases, stock already there. Real, just without a past. |
| Placement | `placement.within_build_same_place` | 95 % | Named moves whose source is where the ledger had the item, when no game update lies in between. Checks the ledger itself. |
| | `placement.brier` | 0.05 | Calibration: mean squared gap between the trust the resolver puts in a placement (through its update weighting) and whether the game then confirmed it. 0 is perfect. |
| | `placement.across_update_same_place` | 90 % | The same agreement across a game update, once the ledger has moved what the update moved to where the player spawned. It is what the resolver's update weighting is learned from. |
| Live | `live.parity` | **100 %** (invariant) | Replaying the busiest files as a growing `Game.log`, one watcher pass per 5 s of log time, gives exactly the single-pass result (every move row and every holding, score and caveats included). |
| | `live.cold_parity` | 95 % | The same, but with a restart before every pass, so only what the store persists carries over. |

Staleness is measured from the corpus's last line, not from today, so a report reads the
same next month.

The report also lists the uncaptured requests by type with example lines, the ledger's
counters, a histogram of the caveats on holdings, and any live tailing differences.

## Study of 2026-09-24

The corpus was 65 local files, 42.8 MB, builds 12344265 through 12660092.

### Before and after

| | Before #11 | Now |
|---|---:|---:|
| `ledger.source_hit_rate` | 5.9 % | **45.5 %** |
| `holdings.inferred_share` | 18.6 % | **4.4 %** |
| `holdings.high_share` | 69.5 % | **80.5 %** |
| `holdings.mean_score` | 0.773 | 0.800 |

The "before" column was measured with this study's code on the tree from before #11.
Within the ledger, #11's only change was to stop dropping moves into a backpack. So the
first two rows are that fix alone. Before it, the move back out of the backpack found an
empty source, so most class-level units were credited as guesses, and the item stayed
recorded at its old station too.

The last two rows also include #11's resolver changes: it removed the relog penalty and
changed how worn containers are placed.

The watcher used to build its ingestor from scratch on every pass, about once a second
during play. Tailing the 2026-08-04 session live that way placed **3 of 15** containers,
because each pass forgot where the player was standing, and it lost 3 geids at pass
boundaries. With parse state carried across passes and the player's location persisted
per session, it places 15 of 15 and matches a single pass exactly (`live.parity` 100 %).

### Carrying items in hand

`Type[Interaction] action[Carry]` is the player taking an item out of a grid into their
hand, usually to eat or drink it. The target is `INVALID` and the caller is
`DoInventoryGridInteractionCB` rather than `AttachItem`, so the move used to be dropped.
Those carries were all 13 uncaptured requests in the local corpus.

Tracing all 13 carries in the local corpus showed two outcomes:

- 8 were stored back into their container within about 15 s, by a `Store` that names the
  entity.
- 5 were never mentioned again: two snacks eaten, two drinks finished, and one mission
  hard drive handed in. Each of the 5 is absent from every body enumeration afterwards,
  while each enumeration re-lists everything the player still holds.

What the tracker does now:

- A carry is recorded as a move onto the player. `move.action` keeps the `Carry`.
- The first named move of an entity takes one unnamed unit of its class out of its
  source, so the crate no longer counts the drink the player is holding. Equips from a
  station benefit the same way.
- A carried item that is missing from the newest body enumeration after the carry is
  treated as used up and leaves the ledger (`ledger.carried_used_up`). The newest
  enumeration is the newest `Body_ItemPort` attachment line plus everything seen within
  30 s of it.
- An item carried after that enumeration stays on the player with a ×0.5 caveat, since
  nothing has contradicted it yet.

| | Before | After |
|---|---:|---:|
| `capture.request_rate` | 96.0 % | **100 %** |
| `holdings.inferred_share` | 4.4 % | 3.0 % |
| `holdings.high_share` | 80.5 % | 81.0 % |
| `ledger.source_hit_rate` | 45.5 % | 44.6 % |

The source-hit rate dips by one unit (62 inferred instead of 61). That fits the double count
being removed: a crate no longer holds a drink that was carried out, so a later class-level
move out of it finds one unit fewer. That particular unit has not been traced.

### Units not found at their source

After the carry fix, 62 of the 112 class-level units in the local corpus still were not
found at their source. The ledger now records every such move with a cause. That showed
the metric was mixing three very different things.

| Cause | Units | What it is |
|---|---:|---|
| First appearance of the class | 42 | Armour pulled out of wreck crates the player never filled, drinks and tools already in a crate or at a station before the logs begin, ammunition bought at a shop. The item provably arrived; it simply has no earlier history. **Not an error**, yet it was scored ×0.8 as "may have been counted twice". |
| Ammunition carried on the player | 15 | For example, 14 magazines moved out of a station that held 1, while the ledger had the other 13 in the backpack: the same total. Magazines ride along in a backpack or on armour and change place with no move line. |
| Class-level `Store` from the hand | 5 | Filled multitool canisters put away from the hand. A Store's source is always the player, but the log writes it as `INVALID`, so the canister stayed equipped as well as stored. |

Three changes follow from that:

- **A shortfall is drawn from what the player carries** (equipped items, backpacks and
  worn apparel) before anything is guessed. This only applies when the move names a real
  source.
- **A class-level Store takes its item off the player.**
- **An arrival with no source is only a duplicate risk if its class is still recorded
  elsewhere.** Otherwise it is a first appearance: it costs no score, and it carries a
  caveat saying it was looted, bought, or there before the logs begin.

`ledger.source_hit_rate` is replaced by two metrics that separate those cases:
`ledger.accounted_rate` and `ledger.duplicate_risk_rate`.

| | Before | After |
|---|---:|---:|
| Units counted as possible duplicates | 62 / 112 | **2 / 112** (`duplicate_risk_rate` 1.8 %) |
| Units found on the player | — | 15 |
| First appearances | (counted as duplicates) | 42 |
| `holdings.inferred_share` | 3.0 % | **0.5 %** |
| `holdings.high_share` | 81.0 % | 82.7 % |

The 2 units still at risk were taken from the player while the ledger also had the class
dropped on the ground (`World`).

### Where the local corpus stands

| Metric | Value | Target |
|---|---:|---:|
| `capture.request_rate` | 100 % | 98 % |
| `capture.unrecognised_lines` | 0 | 0 |
| `enrich.geid_coverage` | 81.3 % | 85 % |
| `enrich.confirmed_rate` | 91.3 % | 95 % |
| `ledger.accounted_rate` | 98.2 % | 95 % |
| `ledger.duplicate_risk_rate` | 1.8 % | 2 % |
| `holdings.high_share` | 82.7 % | 80 % |
| `holdings.low_share` | 4.6 % | 5 % |
| `holdings.mean_score` | 0.806 | 0.85 |
| `holdings.inferred_share` | 0.5 % | 5 % |
| `holdings.first_seen_share` | 2.0 % | 15 % |
| `live.parity` | 100 % | 100 % |
| `live.cold_parity` | 82.9 % | 95 % |

### Age, and game updates

`holdings.mean_score` was held back mostly by age: rows older than 30 days took ×0.8. To test
that assumption, the ledger now checks every named move out of a real source against where
it believed the item was (`ReplayStats.PlacementChecks`). Containers are followed out to the
place they sit in, since an item in a backpack stored at a station is at that station.

| Age of the placement | Within one game build | Across a game update |
|---|---:|---:|
| < 1 h | 19/19 | — |
| 1 h – 1 d | 10/10 | — |
| 1 – 7 d | 4/4 | — |
| 7 – 30 d | 1/1 | **0/13** |

Age never contradicted a placement. A game update contradicted every one it could be checked
on. Build 12519617 was first played on 2026-08-27, and in that first session the player
moved 13 items out of **Area18**:

- armour stored at Lorville 15 days earlier;
- a rifle stored at Levski;
- six items last seen equipped.

Area18 is where the player spawned after the update. The server had moved everything, and
no line of the log says so.

What the tracker does now:

- The build of every log is recorded (`session.build`, from the backup's file name or from
  the live `Game.log`'s header). A new build is a game update.
- **The age penalty is gone.** Age past 30 days is still mentioned, at no cost.
- **A placement made before an update is weighed by what the logs showed of that update:**
  the share of checked placements that survived it, clamped to between ×0.2 and ×1.0. It
  takes at least 5 checks to count as verified; an unverified update costs ×0.5. On this
  corpus that gives ×0.2 for 12519617 (0/13) and ×0.5 for 12572603 and 12660092, which have
  no evidence yet.
- The caveat says which update, and what it did: "placed before the game update to build
  12519617, after which 13 of 13 items checked were not where the ledger had them".

| | Before | After |
|---|---:|---:|
| `placement.brier` | 0.277 | **0.011** |
| `holdings.high_share` | 82.7 % | 11.7 % |
| `holdings.low_share` | 4.6 % | 74.6 % |
| `holdings.mean_score` | 0.806 | 0.322 |

The Brier score is the point of the change. Before it, every checked placement was trusted
fully (all under 30 days old), and 13 of 47 were wrong. After it, the trust matches what
happened. Be aware that it is in-sample: the update factors are learned from those same
checks, so the real test is the next corpus.

The holdings numbers fall because nearly all of this corpus's activity predates the update
that moved everything. Those rows really are unreliable: 147 of them were placed before
12519617, and the evidence says they are most likely at Area18 now. `holdings.high_share`
and `holdings.mean_score` measured optimism as much as reliability. Their targets assume a
corpus played mostly within the current build.

### Moving belongings to the spawn station

Demoting a placement says the item is probably not there. It does not say where it is.
The evidence did: all 13 items were at the station where the player first spawned after the
update. So the ledger now moves them there.

- Every session records the first place the player arrived at (`session.first_loc_id`). The
  first session of a new build that reached a location gives that update's spawn station.
- At the moment an update is first played, the ledger moves everything at a station or on
  the player to that spawn station (`LedgerReplay.Relocation`). Items inside a container go
  with it. Dropped items stay where they fell. Items carried in hand stay too, since they
  are most likely used up, and the next body enumeration settles that.
- Moved items keep their last-seen time and carry the update that moved them. Their caveat
  reads "moved here by the game update to build N, which no log line records". Items the
  player still wears are put back on the player by the next body enumeration.
- The update's weight is still learned from the checks. They now test the relocation, not
  the old place: after 12519617, 13 of 13 items were where the ledger had moved them, so
  that update costs nothing. The two later updates are unverified, so they cost ×0.5 and
  say the move is an assumption.

| | Demote only | Relocate |
|---|---:|---:|
| `placement.across_update_same_place` | 0 % (0/13) | **100 % (13/13)** |
| `placement.brier` | 0.011 | **0** |
| `holdings.mean_score` | 0.322 | 0.422 |
| `holdings.high_share` | 11.7 % | 11.7 % |
| `holdings.low_share` | 74.6 % | 76.1 % |
| `ledger.relocated_by_update` | — | 303 units |

Relocation settles where the items are. It does not raise how far they are trusted. Most of
this corpus's belongings now sit at the spawn station of 12660092, which costs them twice:

- that update is unverified (×0.5);
- the station's id was never paired with a name (×0.7, on 140 rows).

The Brier score of 0 is in-sample, as before, and rests on one update. If a later update
does not move belongings, its checks will show that. Its factor will fall toward ×0.2, and
the caveat will say how many items were not where the ledger moved them.

### Naming the spawn, and when an update moves the player

Trying to name the spawn of 12660092 showed that it is not a station at all. Id
3108822724 is open space by an asteroid mining site. The player quantum-jumped there in a
mining ship on 2026-09-12, logged out in it, and has spawned there in every session since,
before and after the update. The logs name the place in only one way: the quantum point
the player jumped to right before entering it, `ab_mine_stanton3_sml_003`.

**Naming by quantum arrival.** The parser now reads the selected quantum point and the
drive's arrival. The first location entered within 10 seconds of arriving gets that point
as `arrival` evidence. `PlaceCatalog` uses it only as a last resort, after a hand-written
override, a route name and the game's inventory name. The name reads "near
ab_mine_stanton3_sml_003", and the holding says the name is ours, not the game's.

The check against the corpus: for every id the game also paired with an inventory name,
the arrival pointed to the same place, 7 stations out of 7. Arrivals go wrong in two ways,
and both are filtered:

- Mission beacons (`MISSION_QT_…`) are one-off points and are never recorded.
- A system's open space is entered on the way to many destinations: Nyx's collected
  arrivals for 5 different points. A point needs 75 % of an id's arrivals to name it.

One id (ArcCorp's orbit) got an arrival for a mining site on its way there. A route
plotted from it names it "ArcCorp", which takes precedence.

**Relocating only when an update moved the player.** Naming the spawns made them readable:

| Update | Last place before | First place after | Items checked |
|---|---|---|---:|
| 12519617 | Stanton Gateway | **Area18** | 13, all moved to Area18 |
| 12572603 | Orison | Orison | none |
| 12660092 | near `ab_mine_stanton3_sml_003` | same | none |

Only 12519617 put the player somewhere new. After the other two, the player picked up
where they had logged out, and nothing says those updates moved anything. Relocating to
"the spawn" there meant moving every stored item to wherever the player happened to log
out, here a mining ship in open space.

Now each update records where the player last was before it (`GameUpdate.LeftFrom`, the
last `cur_loc_id` an earlier session recorded). Belongings are relocated only when the spawn
differs, comparing ids through place aliases. An update that did not move the player is
weighed as unverified. Its caveat says the player picked up where they had logged out, so
nothing says whether it moved anything. When no earlier session ever placed the player,
`LeftFrom` is empty. That update moves nothing either, and its caveat says the logout place
is unknown. The three updates in this corpus all have one, so the numbers below are unchanged.

| | Relocate to any spawn (#17) | Only when the player was moved, spawns named |
|---|---:|---:|
| `holdings.low_share` | 76.1 % | **5.1 %** |
| `holdings.mean_score` | 0.422 | **0.529** |
| `holdings.high_share` | 11.7 % | 11.7 % |
| `ledger.relocated_by_update` | 303 units | 93 units |
| rows at an unnamed station | 140 | **0** |
| `placement.across_update_same_place` | 100 % | 100 % |

`holdings.high_share` is now held down by one thing: 174 rows cross 12572603 or 12660092,
and an unverified update costs ×0.5. Nothing in this corpus says whether an update that
leaves the player in place moves stored items. The next corpus can say so as soon as one
item comes out of a station it was stored at before 12572603.

## Improvement plan

This is ranked by expected effect on the metrics above. Each item names the metric that
should move, so it can be verified by rerunning the study.

1. ~~Find out why class-level units miss their source.~~ Done: see "Units not found at
   their source" above. ~~Test the staleness penalty.~~ Done: see "Age, and game updates".
   ~~Relocate instead of only demoting.~~ Done: see "Moving belongings to the spawn
   station". ~~Name the spawn stations.~~ Done: see "Naming the spawn, and when an update
   moves the player". Next, a decision for the next corpus: what an update that leaves the
   player in place costs. It is ×0.5 today, on no evidence either way, and it holds
   `holdings.high_share` at 11.7 %. One item taken out of a station it was stored at before
   12572603 would settle it.
   Moves: `holdings.high_share`, `holdings.mean_score`.
2. ~~Decide what `Type[Interaction] action[Carry]` means.~~ Done: see "Carrying items in
   hand" above. `<[ActorState] Place> … placed '<class>_<geid>' in lootable container` is a
   related, unparsed signal: a carried mission item being handed in.
3. **More geid sources.** In the current format, `<Player Inventory Request Complete> …
   Type[Interaction] … Item[<geid>]` names the equipped entity directly, and `<StoreItem>
   store '<class>_<geid>'` names each item of a batch store. Neither is parsed today; both
   would back up the `<UnstowPendingEntities>` pairing, which depends on a 60 s window.
   Moves: `enrich.geid_coverage`, `live.cold_parity`.
4. **Persist pending callers and spawns per session**, not only the player's location, so a
   restart mid-session loses less.
   Moves: `live.cold_parity`.
5. **Confirm `<OnInventoryStoreItem>` rows.** They are never linked to a completion, so a
   placement that rests on one is scored ×0.5.
   Moves: `enrich.confirmed_rate`, `holdings.mean_score`.
6. **`PlaceInstance` looks up an entity's old slot by the new move's class.** When two lines
   spell the class differently, the old entry is never removed and the item appears twice.
   Moves: `holdings.inferred_share`.
7. **Use negative evidence from `<AttachmentReceived>`.** Each spawn lists everything worn,
   so an item the ledger believes equipped but that is missing from the latest list should
   be demoted.

## Adding a corpus

A richer log helps most when the session includes the things the ledger struggles with:

- drags between crates;
- a backpack filled at one station and emptied at another;
- multi-select drags and a batch store;
- a drop;
- eating or drinking from a container;
- a relog in the middle.

1. Run the local study on it (see above) and read `artifacts/reliability/<name>.md`.
2. To commit it as a fixture:
   - keep only the inventory lines (the tags listed in `InventoryEventParser.Parse`);
   - replace the player name;
   - put the files under `Reliability/Corpus/<name>/logbackups/`, keeping their original
     `Game Build(…)` file names;
   - add a test like `Sample_corpus_keeps_its_invariants_and_does_not_regress` and record
     its baseline.
