# 2. One Lua script per subject, all subjects in one pipelined round trip

- Status: Accepted
- Date: 2026-11-23

## Context

For each subject an assessment must trim old events, add the new attempt, refresh the TTL and read several windows. Done as separate commands, two concurrent attempts for the same card can both read "3" before either writes, and both pass a "max 3" rule. That race is exactly what a card-testing script produces.

An assessment touches up to four subjects. On Redis Cluster those keys live in different hash slots, so a single multi-key script or a `MULTI` over all of them is not allowed.

The latency budget is tight: the whole assessment should cost one network round trip to Redis.

## Decision

- `Scripts/record_attempt.lua` handles **one subject**: `ZREMRANGEBYSCORE` (trim), `ZADD NX` (add), `PEXPIRE` (TTL), then `ZCOUNT` or a `ZRANGE BYSCORE` sum per requested window, and returns the values as an array. Redis runs scripts atomically, so read and write cannot interleave with another attempt for that subject.
- A subject's two keys share a hash tag (`vg:{device:dev-7f3a}:att` and `...:amt`), so the script only touches one slot and is valid on a cluster.
- `RedisVelocityStore` sends the scripts for **all subjects plus the journal write** in one StackExchange.Redis batch. The client writes them back to back and reads the replies together: one round trip on a single node, and on a cluster one round trip per node, in parallel.
- StackExchange.Redis caches the script SHA and uses `EVALSHA`, falling back to `EVAL` after a `NOSCRIPT`.
- The outcome transition (`none → approved | declined`, `approved → chargeback`) is a second small script, `set_outcome.lua`, so two callers reporting different outcomes cannot both win.

## Consequences

- Atomic per subject, not across subjects. A device and a player window are updated in separate scripts; a crash between them could leave one updated and not the other. For risk counters that is harmless, and it is the price of running on a cluster.
- The integration suite fires 400 concurrent attempts at one subject from four separate client connections and checks that the returned counts are exactly 1 to 400: no lost or duplicated update.
- The transition rules exist twice, in C# (`OutcomeRules`) and in Lua. The same contract test runs against both stores to keep them in step.
- Scripts are plain files embedded in the assembly, readable and reviewable on their own.

## Alternatives considered

- **`MULTI`/`EXEC` with `WATCH`**: optimistic retries under exactly the contention we care about, and still one slot per transaction.
- **One script for all subjects**: simpler, but breaks on Redis Cluster unless every key shares one hash tag, which would put all traffic on one shard.
- **Redis Functions (`FUNCTION LOAD`)**: a good fit later; scripts were simpler to ship and test today, and the switch would not change the data model.
