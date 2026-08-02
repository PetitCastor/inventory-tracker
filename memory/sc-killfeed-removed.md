---
name: sc-killfeed-removed
description: Star Citizen builds 4.6+ no longer log Actor Death / CActor::Kill / Vehicle Destruction — killfeed parsers are dead
metadata: 
  node_type: memory
  type: reference
  originSessionId: 638219a8-3c05-4664-be5a-0de7b5cdda12
  modified: 2026-08-01T13:25:54.730Z
---

CIG removed death logging from the client. Verified by scanning all 603 backup logs (builds 11319298 → 12344265, versions 4.6.172 → 4.9.188): **`Actor Death`, `CActor::Kill:`, `Vehicle Destruction`, `Kill:`, `ActorStall`, `IncapacitatedTimer` return exactly 0 matches across the entire corpus.**

**Why:** every third-party SC killfeed/overlay tool is built on the `<Actor Death> CActor::Kill: 'victim' [id] ... killed by 'killer' [id] using 'weapon'` line. That line no longer exists, so those tools silently report nothing on modern builds — do not assume a parser is broken on our side.

**How to apply:** don't waste a scan pass looking for death events. Nearest available surrogates, in descending usefulness: insurance claims (`New Insurance Claim Request`, 854 hits) as a ship-loss proxy; `Adding non kept item [CSCActorCorpseUtils::PopulateItemPortForItemRecoveryEntitlement]` (5518 hits) which fires on player death item recovery; `<Corpse>` tags (present in 93 of 603 files). None of them name a killer or a weapon.

See [[sc-log-grammar]] for what *is* extractable.
