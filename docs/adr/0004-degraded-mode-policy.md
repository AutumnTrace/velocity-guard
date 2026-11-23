# 4. Degraded mode: a configurable fail-open / review / fail-closed policy

- Status: Accepted
- Date: 2026-11-23

## Context

The risk check sits on the payment path. When Redis is slow or down, the service can do one of three things, and each has a cost:

- **Fail open** (approve on whatever rules still run): deposits keep flowing; fraud that only velocity would catch gets through.
- **Fail closed** (decline): no fraud gets through; neither does any honest deposit. For a casino on a Saturday night, that is the most expensive option.
- **Error** (5xx): the caller (the payment gateway) has to guess, usually after waiting for a timeout.

Which cost is acceptable is a business decision that changes with context (a big sporting event, a known attack), so it must not be hard-coded.

## Decision

- Every Redis call goes through one Polly pipeline: a **circuit breaker** (opens at 50 % failures over at least 20 calls in 10 s, stays open 15 s) around a **100 ms timeout**. The client fails fast while disconnected instead of queueing. HybridCache L2 calls use the same pipeline.
- Failures become `DependencyUnavailableException("velocity-store" | "risk-data", reason)`. The assessment service catches it per signal, marks the rules that needed it as **skipped**, and evaluates the rest. Amount thresholds, country mismatch and new-account rules still work without Redis.
- `Degraded:Policy` then decides: `FailOpen` (keep the rules' decision), `Review` (at least review; the default) or `FailClosed` (decline). It is read through `IOptionsMonitor`, so it can be changed at runtime.
- The response says so: `degraded: { policy, unavailable, skippedRules, rulesDecision }`, and `velocityguard.degraded{policy, dependency}` counts it.
- Readiness reports **Degraded (HTTP 200)**, not Unhealthy, when Redis is down: the instance can still answer under the policy, and taking every instance out of the load balancer at once would turn a Redis blip into a full outage.
- Writes that cannot be faked (outcomes, list edits) return **503 with `Retry-After`** instead of pretending.

## Consequences

- A Redis outage turns into fast, explicit, measurable decisions. The tests cover each policy, a closed Redis port (unit level, real client) and a `docker pause` on a real Redis container (integration), including recovery after the breaker's half-open probe.
- `Review` as the default pushes the cost onto the manual review queue during an outage. That queue needs to be sized for it, or the policy switched to `FailOpen` with tighter amount rules.
- Attempts made while degraded are not counted in Redis. Velocity is slightly under-counted after an outage; the windows heal as new attempts arrive.

## Alternatives considered

- **Always fail open**: what many in-house checks do implicitly. Rejected as a default because it is invisible: nobody notices the risk engine stopped working.
- **Retry inside the request**: Polly retries would add latency exactly when Redis is struggling. The breaker plus a short timeout gives a better worst case.
- **Local fallback counters while Redis is down**: per-instance counts are wrong in a multi-instance deployment and would need reconciliation; not worth it for outages measured in seconds to minutes.
