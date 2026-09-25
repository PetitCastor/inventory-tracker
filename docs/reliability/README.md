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
| Ledger | `ledger.source_hit_rate` | 80 % | Class-level units found where the move said they came from. Each miss is history the ledger never saw, and a likely duplicate. |
| Holdings | `holdings.high_share` | 80 % | Rows rated High confidence, the ones the user can trust outright. |
| | `holdings.low_share` | 5 % | Rows rated Low. |
| | `holdings.mean_score` | 0.85 | Mean resolver score. |
| | `holdings.inferred_share` | 5 % | Rows that rest on an arrival whose source was never seen. |
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

### Where the local corpus stands

| Metric | Value | Target |
|---|---:|---:|
| `capture.request_rate` | 96.0 % | 98 % |
| `capture.unrecognised_lines` | 0 | 0 |
| `enrich.geid_coverage` | 80.6 % | 85 % |
| `enrich.confirmed_rate` | 91.0 % | 95 % |
| `ledger.source_hit_rate` | 45.5 % | 80 % |
| `holdings.high_share` | 80.5 % | 80 % |
| `holdings.low_share` | 4.4 % | 5 % |
| `holdings.mean_score` | 0.800 | 0.85 |
| `holdings.inferred_share` | 4.4 % | 5 % |
| `live.parity` | 100 % | 100 % |
| `live.cold_parity` | 82.8 % | 95 % |

## Improvement plan

This is ranked by expected effect on the metrics above. Each item names the metric that
should move, so it can be verified by rerunning the study.

1. **Find out why 61 of 112 class-level units still miss their source.** For 18 class-level
   moves the ledger has nothing at all at the source. The rest find fewer units than they
   ask for. Candidates to check against the report:
   - history that starts mid-stream, before the corpus;
   - loot pulled from containers the ledger never saw filled;
   - moves out of a crate whose contents were only ever credited by class.

   Moves: `ledger.source_hit_rate`.
2. **Decide what `Type[Interaction] action[Carry]` means.** Picking an item into your hands
   to eat or drink it accounts for all 13 uncaptured requests. The target is `INVALID` and
   the caller is `DoInventoryGridInteractionCB` rather than `AttachItem`, so it is dropped,
   and the item stays in its container forever. The item is most likely consumed, so the
   move should at least take it out of its source.
   Moves: `capture.request_rate` (to 100 %), `holdings.*`.
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
