---
name: sc-parse-cutoff-49
description: Standing rule - parse only 4.9 logs (build >= 12232306, from 2026-07-16); never widen to older builds
metadata:
  type: project
---

**Hard stop: only parse logs from 2026-07-16 onward — build `12232306` and later, client 4.9.**

Filter by **build id**, not date: `Build\((\d+)\)` from the filename, keep `>= 12232306`. A session that rotates past midnight would leak 4.8 data into a date window.

The boundary is exact and verified:

| Build | FileVersion | Log dates |
|---|---|---|
| 12122953 | 4.8.184.64329 | 2026-07-01 → **2026-07-15** |
| **12232306** | **4.9.186.42610** | **2026-07-16** → 2026-07-17 |

4.9 builds in the corpus: `12232306`, `12248363`, `12269732`, `12302499`, `12326004`, `12344265` — **56 of 603 files**.

**Why:** pre-4.9 uses a different, poorer inventory grammar (no `<Add Inventory Management Move>`, no completion lines, no Hands/body-port side). Mixing eras silently halves data quality rather than erroring — with the cutoff 90% of item movements carry both endpoints, without it only 37% do. Old logs are also stale relative to current game systems.

**How to apply:** every new scan script starts from the 4.9 file set. `scratchpad/extract-item-moves.ps1` implements it as `-MinBuild 12232306` with an `-IncludeLegacy` escape hatch — that switch is for grammar archaeology only, never for producing numbers. Corpus-wide figures recorded in [[sc-log-corpus]], [[sc-log-noise-floor]] and the census in [[sc-log-grammar]] predate this rule and span all 603 files; re-derive before quoting them as current. See [[sc-item-movement]] for what the 4.9 grammar yields.
