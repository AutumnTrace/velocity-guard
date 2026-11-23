# 3. HybridCache (memory + Redis) for player profiles and risk lists

- Status: Accepted
- Date: 2026-11-23

## Context

Besides velocity, an assessment needs a player profile (approved deposits, chargebacks) and four or more list checks (blocked devices, blocked cards, blocked IPs, trusted players). These change rarely and are read on every payment. A Redis round trip per lookup would multiply the latency of the hot path; a plain in-process cache would leave every instance with its own idea of who is blocked.

Two kinds of change happen:

- **Point changes**: one device added after a chargeback, one profile updated after an approved deposit.
- **Bulk changes**: an analyst replaces a whole list.

## Decision

Use `Microsoft.Extensions.Caching.Hybrid` through a small wrapper, `RiskSignalCache`:

- **L1** is in-process memory (30 s by default), **L2** is Redis (5 min), the system of record is a repository (Redis sets and hashes, or in-memory for local runs). HybridCache adds stampede protection: concurrent misses for the same key share one load.
- List lookups are cached **per entry**, including negative answers (`list:blocked-devices:dev-7f3a → false`). Most lookups are "not listed", and those are the ones worth caching.
- **Point changes evict the key** with `RemoveAsync`. That removes it from L1 and from L2, so other instances miss in L2 and reload.
- **Bulk changes invalidate by tag**: every list entry is tagged `vg:list:<name>`, and replacing a list calls `RemoveByTagAsync`. One call makes every cached answer for that list stale, positive or negative, without enumerating keys. HybridCache records the invalidation in L2, so an instance that reads L2 afterwards also rejects the stale entries (covered by an integration test with separate cache instances).
- `PlayerProfile` is a sealed record marked `[ImmutableObject(true)]`, so an L1 hit returns the cached instance without deserialising a copy.
- L2 calls go through the same circuit breaker as every other Redis call (`ResilientDistributedCache`), so a hung Redis costs one 100 ms timeout, not the client's own timeout on every lookup.

## Consequences

- An L1 hit costs a dictionary lookup and a few small allocations; see the benchmarks in the README.
- **There is no backplane.** Another instance can serve a stale L1 entry for up to `Cache:LocalExpiration` (30 s) after a change. For a blocklist that is an explicit, bounded trade-off: the instance that processed the chargeback blocks immediately, the rest converge within 30 s. Lower the setting if that is too long.
- A Redis outage degrades gracefully: L1 keeps answering what it has, misses fall through to the repository, and the repository's failure triggers the degraded-mode policy ([ADR 4](0004-degraded-mode-policy.md)).
- Cache metrics: `velocityguard.cache.lookups` and `velocityguard.cache.misses` by cache, so the hit ratio is one division.

## Alternatives considered

- **`IMemoryCache` plus Redis pub/sub invalidation**: a real backplane, but more code to own (subscription lifetime, reconnects, missed messages). Worth it if 30 s convergence ever becomes a problem.
- **Load whole lists into memory and refresh periodically**: simple and fast for small lists, but memory grows with the list and refresh lag applies to every entry.
- **No cache, Redis per lookup**: correct and simple; costs about five extra round trips per assessment.
