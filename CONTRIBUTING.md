# Contributing

Thanks for taking a look. This is a small service with a few strong opinions; this page lists them so a change lands smoothly.

## Getting started

```bash
git clone https://github.com/AutumnTrace/velocity-guard.git
cd velocity-guard
dotnet build
dotnet test --filter "Category!=Integration"   # no Docker needed
```

The integration tests start Redis with Testcontainers and need Docker. CI runs them on every pull request:

```bash
dotnet test --filter "Category=Integration"
```

## Ground rules

- **Money is never a `double`.** Amounts are `long` minor units with an explicit ISO 4217 currency (`Money`). Limits in configuration are major units and are converted once, at compile time.
- **No card data.** The API accepts PSP tokens or fingerprints, never PANs; values shaped like a card number are rejected and never echoed or logged. Logs carry attempt ids and rule ids, not IPs or device ids.
- **The rule loop does not allocate.** `CompiledRuleSet.Evaluate` has a test that asserts zero allocated bytes. If you add a rule type, keep it that way: resolve everything you can in `RuleCompiler`.
- **Both stores obey one contract.** Behaviour of the velocity store is pinned by `VelocityStoreContract`, which runs against the in-memory store (unit) and Redis (integration). Change both or neither.
- **Lua and C# agree.** Outcome transitions exist in `OutcomeRules` and in `set_outcome.lua`; window semantics in `InMemoryVelocityStore` and `record_attempt.lua`.
- **Degrade, do not fail.** Reads that can be answered under the degraded-mode policy must not throw to the caller; writes that cannot be faked return 503.

## Adding a rule type

1. Add the type to `RuleType` and its parameters to `RuleDefinition`.
2. Validate them in `RiskRulesValidator` (every failure reported, with the rule id).
3. Resolve them in `RuleCompiler` (slots, bits, minor units) and evaluate them in `CompiledRuleSet.EvaluateRule`.
4. Explain hits in `RuleExplainer`, expose parameters in `RuleResponse`.
5. Tests: hit, pass, not applicable and skipped in `RuleEvaluationTests`; validation cases in `RiskRulesValidatorTests`.

## Commits and pull requests

- [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `perf:`, `test:`, `docs:`, `chore:`).
- One coherent change per pull request, with a summary, the changes and how you tested them.
- Update `CHANGELOG.md` under **Unreleased**.
- Performance claims need BenchmarkDotNet numbers from a real run, with its environment line.
