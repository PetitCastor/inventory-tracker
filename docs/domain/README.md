# Domain notes

Reverse-engineering notes on Star Citizen's `Game.log` — the research the parser and the
resolver are built from. They record what the game actually writes, which of it is
trustworthy, and what was tried and found not to work.

These are also the natural fixture source for `tests/InventoryTracker.Tests`: the log lines
quoted here are real records.

- [Parse cutoff: 4.9 only](sc-parse-cutoff-49.md) — **hard stop**: parse only build >= 12232306 (2026-07-16+). Read before writing any scan.
- [SC log corpus](sc-log-corpus.md) — where the logs live, 603 files / 917MB / Mar-Jul 2026, session + crash stats.
- [SC log grammar](sc-log-grammar.md) — line format and verified regex anchors for every extractable gameplay signal.
- [Killfeed removed in 4.6+](sc-killfeed-removed.md) — Actor Death / CActor::Kill log 0 hits corpus-wide; don't go looking.
- [Log error noise floor](sc-log-noise-floor.md) — which errors are background vs. real; normalise to errors/minute.
- [Item movement ledger](sc-item-movement.md) — from→to extraction via Request[N] join; per-type endpoint semantics; batch moves; three logging eras, split at build 12232306 and again at build 12519617; geid stability across sessions.
- [Location ids](sc-location-ids.md) — `<player>:Location:<id>` is player-led; how to name the numeric id from `<RequestLocationInventory>`; not a container.
- [Wiki name resolution](sc-wiki-naming.md) — api.star-citizen.wiki partial-match gotcha, Found/NotFound/Error caching rule, empty-ItemClass crash, cache file location.
