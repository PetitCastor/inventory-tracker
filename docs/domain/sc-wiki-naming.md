---
name: sc-wiki-naming
description: Human-readable item names via api.star-citizen.wiki — API shape, caching rules, and the empty-ItemClass crash
metadata:
  type: reference
---

Finder resolves raw class names (`Drink_bottle_cruz_01_lux_a`) to human names (`CRUZ Lux`) via `api.star-citizen.wiki`, no auth. Code: `src/StarCitizen.Finder/Naming/`.

**API.** `GET api/items?filter[class_name]=<value>` — **partial match, not exact**. Must scan `data[]` for the entry whose own `class_name` field equals the query exactly; never take `data[0]`. Rate limit 60 req/min, non-commercial use only. Confirmed live 4/4 on real corpus names.

**3-state result is load-bearing, not over-engineering.** A plain `string?` conflates "wiki has no match" with "request failed" — caching a timeout as a miss would permanently poison the cache for every item touched during an outage. `WikiLookupResult{Found|NotFound|Error}`: only `Found`/`NotFound` get cached, `Error` always retries next run.

**Empty `ItemClass` crashes if not guarded.** Real corpus rows exist with an empty class string. `LookupKey.From` must return `null` (not `""`) for these, or the empty string reaches `NameResolver.ResolveAsync` → `ArgumentException.ThrowIfNullOrWhiteSpace`. Regression test: `LookupKeyTests.Given_a_class_keyed_instance_with_an_empty_item_class_When_derived_Then_no_key_is_produced`.

**Throttle/cap/breaker**, all in `NameResolver`: ~1 req/sec, 25 new lookups per run (cache warms progressively across runs), circuit opens after 3 consecutive `Error`s (bounds a fully-offline run to ~15-20s instead of burning the whole cap on timeouts).

**Cache**: `cache/item-names.json`, gitignored, CWD-relative — only correct when run from repo root. `null` value = confirmed negative (distinguishes "never asked" from "asked, no match"). No TTL: a `NotFound` never expires.

**Scope boundary (deliberate, not a gap):** only `EntityInstance.DisplayName` is resolved. Container/location names (`Endpoint.Display`) stay raw — resolving them too would multiply lookup volume against the "lazy, minimal calls" design.
