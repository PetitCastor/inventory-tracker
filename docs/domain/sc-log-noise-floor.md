---
name: sc-log-noise-floor
description: 4.9 error baseline - which errors scale with session length (noise), which fire once per boot, and which actually indicate a problem
metadata:
  node_type: memory
  type: reference
---

**4.9 baseline** ([[sc-parse-cutoff-49]], 56 sessions / 50 h): **14,807 `Error`+`Warn` lines, 4.93 errors/min corpus-wide, median session 4.95/min, range 0.38 to 115.95.** Raw counts are meaningless; always normalise by session duration. Full table: `scratchpad/error-tags-49.tsv`.

**Classify by correlation with session length, not by volume.** A background tag's per-session count scales with how long you played (Pearson r of count vs. minutes across all 56 sessions). Three behaviours:

| Class | Meaning | Share of errors |
|---|---|---|
| **FLOOR** (r >= 0.6, most sessions) | pure duration-driven noise | 5 tags, 6,979 lines, 47% |
| **SCALES** (r 0.3-0.6) | partly duration-driven | 23 tags, 6,332 lines, 43% |
| **EVENT** (r < 0.3, few sessions) | something actually happened | 35 tags, 1,496 lines, 10% |

**FLOOR - ignore.** `CSCLoadingPlatformManager::TransitionLightGroupState` (2,453), `LoadingPlatformUtilities::LoadFromXmlNode` (2,170), `CSCLoadingPlatformManager::loadEntityFromXML::<lambda_1` (2,170), both `CEntityComponentLocationManager::PostInitialize::<lambda>` variants (~93 each). Also largely noise: `[Particle System] Kidnapping Child Effect` (523), `SerializedOverwrite` (313).

**CONSTANT per-boot cost - not noise, not signal.** `[Subsumption::ErrorReporter]` fires in **100% of sessions, 692 total = ~12.4 per boot**, independent of length (r=0.26). `ReplicatedCreationBatchNoCallback` likewise 100% of sessions, ~1.7 each. Subtract these as a fixed offset; a *changed* per-boot count is the only interesting thing about them. (A naive r-threshold mislabels these as EVENT because they don't scale - check `%Files` before believing that.)

**The real diagnostic signal: `CreationRefusedEntity` + `DuplicatedItemPorts`.** They fire as a near-perfect 1:1 pair (1,766 vs 1,724 corpus-wide) and together dominate every bad session - 239+237 of 562 errors in the worst one, 371+369 of 867 in the third-worst. Treat them as one inventory/loadout desync signal, not two. Supporting cast: `Failed attachment` (1,027), `Failed to attach to itemport, destroying entity` (374), `Out-of-order ranks in inventory container` (73, EVENT, 52% of hits in one session).

**Other EVENT tags worth a look:** `Linked entity initializing!` (178, 18% of sessions), `Too many actions` (120, only 3 sessions), `Suscribe error` (95, CIG's typo), `FinalizeParentNotAvailable` (63), `CAttachableComponent::GetPortFromId` (57, **91% in a single session**).

**How to apply.** Normalise to errors/minute; subtract the ~12/boot Subsumption constant; ignore FLOOR tags; then flag sessions above **~10 errors/min** (top ~14% in 4.9). The worst session in 4.9 is `Build(12344265) 31 Jul 26 (11 34 09)` at 115.95/min - 562 errors in 4.8 min, 85% of them the desync pair.

**Grammar caveat.** The second bracket is *not* always a severity. 4.9 also emits `[CVARS]`, `[STAMINA]`, `[VK_INFO]`, `[flow]`, `[VK]`, `[CIG]`, `[PSOCacheGen]`, `[DataCore]` and `Team_*` in that position. Filtering on `[Error]` alone is correct for errors but do not assume the field is a severity enum. See [[sc-log-grammar]].
