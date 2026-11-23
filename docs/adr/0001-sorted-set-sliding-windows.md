# 1. Sliding windows on sorted sets, not fixed buckets

- Status: Accepted
- Date: 2026-11-23

## Context

Velocity rules ask questions like "more than 3 attempts from this device in the last minute" or "more than 2,000 EUR deposited by this player in the last 24 hours". The answer has to be exact at the edges: card testing happens in bursts of a few seconds, and a window that resets on the minute lets a fraudster send 3 attempts at 12:00:59 and 3 more at 12:01:00 without tripping a "3 per minute" rule.

The common options in Redis:

1. **Fixed buckets**: `INCR device:dev-1:202611231200` with a TTL. One integer per bucket, very cheap, but the window jumps at bucket boundaries.
2. **Sliding log**: a sorted set per subject, one member per event, scored by its timestamp. Exact windows, memory proportional to events.
3. **Approximate sliding window**: weight the previous bucket by the overlap. Cheap, but wrong by design, and wrong in the direction an attacker can exploit.

Volumes per subject are small: a player makes a handful of deposits a day, a device a few attempts a minute even when it is testing cards. Exactness matters more than bytes.

## Decision

Each subject (player, device, IP or IPv6 /64, card token) gets two sorted sets in Redis:

- `vg:{device:dev-7f3a}:att`: attempt ids scored by unix-ms time, for count windows.
- `vg:{device:dev-7f3a}:amt`: members `attemptId|EUR|2500`, for per-currency sum windows. Only created for subjects that have a sum rule.

Windows are half-open, `(now - w, now]`, the same in Lua and in the in-memory store. Each set is trimmed to the subject's longest window on every write and carries a TTL of that length, so idle subjects disappear on their own. All windows of one subject (1 minute, 10 minutes, 24 hours) are answered from the same set: one `ZCOUNT` per count window, one `ZRANGE ... BYSCORE` per sum window.

Members are attempt ids, and they are added with `ZADD NX`: a retried assessment is counted once and keeps its first timestamp.

## Consequences

- Windows are exact to the millisecond; tests pin the boundary (`An_event_exactly_one_window_old_is_outside_the_window`).
- Amounts can be taken back out: when a payment is declined (by us or by the PSP) its member is removed from the amount set, so sum windows reflect money that actually moved while count windows still see the attempt.
- Memory grows with events in the longest window. For the shipped rules that is at most 7 days of deposits per card, which is small. A rule such as "IP attempts in 30 days" on a busy NAT address would be expensive; the validator caps windows at 31 days and the README calls it out.
- The timestamp comes from the application clock (`TimeProvider`), not Redis `TIME`. That keeps tests deterministic with a fake clock and keeps scripts pure; the cost is that instances need NTP-synced clocks. A few milliseconds of skew does not matter for minute-scale windows, and out-of-order events are counted correctly (there is a test for it).

## Alternatives considered

- **Fixed buckets with `INCRBY`**: rejected for the boundary problem above. Would be the right choice for very long windows with huge volume (monthly limits per IP range), which this service does not have.
- **Redis TimeSeries or a stream per subject**: more machinery and a module dependency for no gain at these volumes.
- **Counting in the database of record**: a query per assessment on the payment path; too slow and couples the risk check to the ledger's load.
