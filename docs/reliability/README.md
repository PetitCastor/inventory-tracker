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
logging formats.

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

- **an invariant breaks**, on any corpus: a move request line the parser cannot read,
  or live tailing that disagrees with a single pass;
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
| | `capture.unparsed_lines` | **0** (invariant) | Move request lines the parser could not read. Non-zero means the game changed its format. |
| | `capture.fallback_lines` | 0 | Lines read only by shape under an unknown tag. Nothing is lost, but the tag was renamed. |
| Enrichment | `enrich.geid_coverage` | 85 % | Moves that name the exact entity rather than just a class. |
| | `enrich.confirmed_rate` | 95 % | Moves carrying a `succeed` completion. An unconfirmed placement is scored ×0.5. |
| Ledger | `ledger.source_hit_rate` | 80 % | Class-level units found where the move said they came from. Each miss is history the ledger never saw, and a likely duplicate. |
| | `ledger.backpack_skip_share` | 0 % | Moves ignored for landing in a backpack. |
| Holdings | `holdings.high_share` | 80 % | Rows rated High confidence, the ones the user can trust outright. |
| | `holdings.low_share` | 5 % | Rows rated Low. |
| | `holdings.mean_score` | 0.85 | Mean resolver score. |
| | `holdings.inferred_share` | 5 % | Rows that rest on an arrival whose source was never seen. |
| Live | `live.parity` | **100 %** (invariant) | Replaying the busiest files as a growing `Game.log`, one watcher pass per 5 s of log time, gives exactly the single-pass result (every move row and every holding, score and caveats included). |
| | `live.cold_parity` | 95 % | The same, but with a restart before every pass, so only what the store persists carries over. |

Staleness is measured from the corpus's last line, not from today, so a report reads the
same next month.

The report also lists the uncaptured requests by type with example lines, the ledger's
counters, a histogram of the caveats on holdings, the tags that drifted, and any live
tailing differences.

## Study of 2026-09-24

The corpus was 65 local files, 42.8 MB, builds 12344265 through 12660092.

### What this PR fixed

| | Before | After |
|---|---:|---:|
| Moves recorded (full corpus) | 267 | 314 |
| Moves from the 2026-08-26 session (build 12519617) | 5 | 44 |
| Containers placed, 2026-08-04 session tailed live (2 s passes) | 3 / 15 | 15 / 15 |
| Geids recovered, same session tailed live | 55 | 58 |
| Unreadable move request lines, sample corpus | 4 | 0 |

- **Format drift.** Build 12519617 renamed `<InventoryManagementRequest>` to
  `<Inventory Mgmt Request Queued>` and started writing `New Request[` with a capital R.
  Either change alone was enough to stop moves being recorded, silently, from 2026-08-26 on.
  The parser now knows both spellings. It also recognises a request by its body under any
  tag, and it counts the lines it misses; the layout shows a banner when any are missed.
  `InventoryEventParser.Version` forces completed files to be re-ingested whenever the
  parser learns something new, since those files are never reopened otherwise.
- **Live state.** Every watcher pass (about one per second during play) used to start with
  empty parse state. Losing the player's location left most containers unplaced, and
  losing the move-to-spawn pairing cost geids at pass boundaries. The watcher now keeps
  one ingestor, which carries the state from one pass to the next, and the player's
  location is persisted per session so it also survives a restart.
- **Store of an unnamed entity.** A Store whose `Item[]` held a bare class with no geid was
  dropped. It is now recorded as a class-level Store.

### Where the local corpus stands now

| Metric | Value | Target |
|---|---:|---:|
| `capture.request_rate` | 96.0 % | 98 % |
| `enrich.geid_coverage` | 80.6 % | 85 % |
| `enrich.confirmed_rate` | 91.0 % | 95 % |
| `ledger.source_hit_rate` | **5.9 %** | 80 % |
| `ledger.backpack_skip_share` | 29.3 % | 0 % |
| `holdings.high_share` | 69.5 % | 80 % |
| `holdings.mean_score` | 0.773 | 0.85 |
| `holdings.inferred_share` | 18.6 % | 5 % |
| `live.parity` | 100 % | 100 % |
| `live.cold_parity` | 87.0 % | 95 % |

## Improvement plan

This is ranked by expected effect on the metrics above. Each item names the metric that
should move, so it can be verified by rerunning the study.

1. **Track backpack contents instead of ignoring them** (`LedgerReplay.Apply`). 101 of 345
   moves are dropped for landing in a backpack. The move back out then finds an empty
   source, so 64 of 68 class-level units are credited as inferred, and the named instance
   stays behind at its old station as a duplicate. Model the backpack as a holding that
   travels with the player, and keep the last real place for display.
   Moves: `ledger.source_hit_rate`, `ledger.backpack_skip_share`, `holdings.inferred_share`,
   `holdings.high_share`.
2. **Decide what `Type[Interaction] action[Carry]` means.** Picking an item into your hands
   to eat or drink it is all of the 13 uncaptured requests. The target is `INVALID` and the
   caller is `DoInventoryGridInteractionCB` rather than `AttachItem`, so it is dropped, and
   the item stays at its container forever. The item is most likely consumed, so the move
   should at least take it out of the source.
   Moves: `capture.request_rate` (to 100 %), `holdings.*`.
3. **More geid sources.** In the current format, `<Player Inventory Request Complete> …
   Type[Interaction] … Item[<geid>]` names the equipped entity directly. `<StoreItem> store
   '<class>_<geid>'` names each item of a batch store. Neither is parsed today; both would
   back up the `<UnstowPendingEntities>` pairing, which depends on a 60 s window.
   Moves: `enrich.geid_coverage`, `live.cold_parity`.
4. **Confirm `<OnInventoryStoreItem>` rows.** They are never linked to a completion, so a
   placement that rests on one is scored ×0.5.
   Moves: `enrich.confirmed_rate`.
5. **`PlaceInstance` looks up an entity's old slot by the new move's class.** When two lines
   spell the class differently, the old entry is never removed.
   Moves: `holdings.inferred_share`, duplicate count.
6. **Re-check the `SupersededByRelog` assumption.** It assumes geids are re-issued every
   session, but 31 of 148 geids in the corpus appear in several sessions, and the ×0.35
   penalty may hit items that are tracked correctly.
   Moves: `holdings.high_share`, `holdings.mean_score`.
7. **Use negative evidence from `<AttachmentReceived>`.** Each spawn lists everything worn,
   so an item the ledger believes equipped but that is missing from the latest list should
   be demoted.
8. **Persist pending callers and spawns per session**, not only the player's location.
   Moves: `live.cold_parity`.

## Adding a corpus

A richer log helps most when the session includes the things the ledger struggles with:
drags between crates, a backpack filled and emptied at different stations, multi-select
drags, a batch store, a drop, eating or drinking from a container, and a relog in the
middle.

1. Run the local study on it (see above) and read `artifacts/reliability/<name>.md`.
2. To commit it as a fixture, keep only the inventory lines (the tags listed in
   `InventoryEventParser.Parse`), replace the player name, and put the files under
   `Reliability/Corpus/<name>/logbackups/` with their original `Game Build(…)` file names.
   Then add a test like `Sample_corpus_keeps_its_invariants_and_does_not_regress` and record
   its baseline.
