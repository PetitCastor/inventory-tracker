---
name: sc-log-corpus
description: "Shape and location of the Star Citizen 4.9 log corpus (56 files, 81.4 MB, 2026-07-16 to 2026-08-01), re-derived under the 4.9 parse cutoff"
metadata:
  node_type: memory
  type: project
---

Star Citizen client logs live at `E:\Games\StarCitizen\LIVE\Game.log` (current session) and `E:\Games\StarCitizen\LIVE\logbackups\*.log` (rotated, named `Game Build(<buildid>) DD Mon YY (HH MM SS).log`).

**All figures below are 4.9 only** per the [[sc-parse-cutoff-49]] hard stop (build >= 12232306). On disk there are 604 backups / ~917 MB going back to 2026-03-03; **56 files / 81.4 MB qualify** and everything else is out of scope.

Corpus as re-derived 2026-08-01: **56 files, 81.4 MB, 349,580 lines, 2026-07-16 → 2026-08-01, 6 builds.**

| Build | FileVersion | Files |
|---|---|---|
| 12232306 | 4.9.186.42610 | 9 |
| 12248363 | 4.9.186.58667 | 15 |
| 12269732 | 4.9.187.14500 | 11 |
| 12302499 | 4.9.187.47267 | 6 |
| 12326004 | 4.9.188.5236 | 2 |
| 12344265 | 4.9.188.23497 | 13 |

Session stats: **50.04 h total playtime, median 45.4 min, mean 53.6 min, longest 246 min, 7 sessions under 2 min.** Exit causes: 49 `Quit via console command`, 5 `User closed the application`, 2 with no `<SystemQuit>` line (abnormal exit). No back-end-unresponsive exits in 4.9.

**1 `EXCEPTION_ACCESS_VIOLATION`** — 2026-07-18, build 12248363, `addr=0x00007FFE891853C8`, and the log marks it `Is fatal error: No`. Crash-handler lines carry **no `<ISO>` timestamp prefix**, so any scanner that gates on the timestamp before testing for crashes will report zero — that bug already happened once here.

Account: `Arcadiius` only (58 logins). 27 distinct shards. Regions: `use1b` 45, `apse2a` 19, `euw1b` 5, `ape1a` 1.

**The live `E:\Games\StarCitizen\LIVE\Game.log` is NOT in `logbackups` and is excluded from every figure above.** It is a real 4.9 session (checked 2026-08-01: build 12344265, 2,758 lines, ~41 min) and a scan globbing only `logbackups\*.log` silently omits the current session. Include it deliberately when currency matters; it is mutable, so corpus totals stay on the rotated backups.

Audited 2026-08-01: no backup filename lacks a `Build(N)` id (nothing dropped by the build filter), all 56 files yield parsable sessions, zero session-window overlaps, sum of durations 50.04 h against a 376.7 h wall-clock span.

Too large to read directly — scan with a streaming `System.IO.StreamReader` loop, never `Get-Content`. **PowerShell 5.1 mis-parses non-ASCII in script files** (an em-dash in a comment desynced the parser twice here) — keep scan scripts ASCII-only. See [[sc-log-grammar]] for line contents, [[sc-log-noise-floor]] for which errors matter, [[sc-item-movement]] for the item ledger.
