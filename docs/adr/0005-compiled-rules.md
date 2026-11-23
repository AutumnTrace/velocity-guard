# 5. Rules in configuration, compiled into slot-indexed structs, hot-reloaded

- Status: Accepted
- Date: 2026-11-23

## Context

Risk rules change often: a threshold is tuned after a fraud wave, a new rule is tried. Redeploying for each change is slow, and having analysts edit code is risky. On the other hand, rules run on every payment, so interpreting a configuration tree on every request (dictionary lookups by currency, string comparisons, LINQ) would put allocations and hashing on the hot path.

## Decision

- Rules live in `rules.json` (section `Risk`), bound to `RiskRulesOptions` and watched through `IOptionsMonitor`, with `reloadOnChange`.
- `RiskRulesValidator` checks the whole set and reports **every** problem: ids, types, windows (1 s to 31 days, whole milliseconds), limits per known ISO 4217 currency and representable in its minor unit, thresholds `1 <= ReviewAt < DeclineAt <= 100`, at least one enabled rule, at most 64 distinct list checks.
- `RuleCompiler` turns the options into a `CompiledRuleSet`:
  - each rule becomes a `CompiledRule` **struct** in an array;
  - per-currency limits become a `long[]` of minor units indexed by a dense currency id, so a lookup is an array access;
  - velocity windows are de-duplicated into **slots** (two rules on "device, 1 minute" share one), and the store fills a `long[]` of slot values;
  - list checks are de-duplicated into **bit positions**, and the cache returns a `ulong` hit mask;
  - a content hash becomes the **rules version**, returned with every decision.
- Evaluation is a loop over the struct array with a `switch` on the rule type, writing into a caller-provided `Span<RuleResult>` (stack-allocated for up to 128 rules). No allocation; a unit test asserts zero bytes over 1,000 evaluations, and BenchmarkDotNet's memory diagnoser shows `-`.
- `RuleSetProvider` is **strict at startup** (invalid rules stop the service from starting) and **forgiving on reload** (an invalid edit is logged, counted as `velocityguard.rules.reloads{result=rejected}` and ignored; the last good set keeps serving). Swaps are a single reference write; requests in flight finish on the set they started with.
- Rules can run in **shadow mode**: hits are reported in `shadowHits` and counted in metrics, but never change the score or decision.

## Consequences

- Changing a threshold is a file edit with no restart, and the version in each response shows which rules decided it.
- The validator is the real safety net: a bad edit cannot take the service down or silently empty the rule set.
- Adding a new rule type means adding a case to the compiler and the evaluator. That is deliberate: rule types are code (tested, reviewed), rule instances are data.
- The JSON file is the only source of rules. A rules UI or a database-backed source would plug in as another configuration provider without changing the engine.

## Alternatives considered

- **Expression trees compiled to delegates per rule**: flexible, but harder to read and debug for little gain over a `switch` on a small, closed set of types.
- **A general rules engine or DSL** (e.g. a scripting language): powerful, but puts arbitrary code on the payment path and needs its own sandboxing and tests.
- **Rules in a database**: better for many authors and audit trails; overkill for this scope, and still compatible with the compiled model later.
