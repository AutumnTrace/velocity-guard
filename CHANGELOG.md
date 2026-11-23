# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] - 2026-11-23

First release.

### Added

- **Assessments**: `POST /v1/assessments` returns `approve`, `review` or `decline` with a 0–100 score, the reasons (human-readable), every evaluated rule, the rules version and the server-side latency (#1)
- **Rules engine**: rules in `rules.json`, validated as a whole and compiled into slot-indexed structs; evaluation does not allocate. Nine rule types: velocity count, velocity sum, amount threshold, BIN/IP country mismatch, new account with a large amount, first deposit, chargeback history, blocklist, allowlist. Forcing actions (`Review`, `Decline`) and shadow mode (#1)
- **Hot reload** through `IOptionsMonitor`: strict at startup, invalid edits rejected and counted while the last good rule set keeps serving (#1)
- **Outcomes**: `POST /v1/assessments/{id}/outcome` feeds back `approved`, `declined` or `chargeback`; declines release the amount from sum windows, chargebacks flag the player and block the card and device (#1)
- **Signals cache**: player profiles and list memberships through HybridCache (L1 memory, L2 Redis), key eviction for single changes and tag invalidation for whole-list replacements (#1, #2)
- **Admin list API** behind `X-Admin-Key` with a constant-time comparison (#1)
- **Redis storage**: sorted-set sliding windows updated by one atomic Lua script per subject, all subjects in one pipelined round trip, cluster-safe hash tags, attempt journal with atomic outcome transitions, lists and profiles (#2)
- **Resilience**: one Polly pipeline (circuit breaker around a 100 ms timeout) for every Redis call including HybridCache L2; configurable degraded-mode policy (`FailOpen`, `Review`, `FailClosed`) reported in the response and in metrics; readiness reports `Degraded` rather than dropping out of rotation (#2)
- **gRPC**: `velocityguard.v1.RiskService/Assess` on port 8081 with amounts in minor units, sharing validation and velocity with REST (#3)
- **Benchmarks**: BenchmarkDotNet suite for rule evaluation, the cache hit and miss paths, the in-memory store and a full in-process assessment (#4)
- **Observability**: Serilog (compact JSON), OpenTelemetry traces and metrics (decisions, rule hits, latency histograms, cache lookups and misses, degraded decisions, rule reloads, outcomes) with OTLP and Azure Monitor exporters switched on by configuration (#1)
- **Delivery**: multi-stage Dockerfile on a chiseled non-root image, `docker compose` stack with Redis and the Aspire dashboard, CI with unit and Testcontainers integration tests, coverage summary, container and compose smoke tests (REST and gRPC), CodeQL, GHCR publishing on tags (#1, #2, #3, #5)

[Unreleased]: https://github.com/AutumnTrace/velocity-guard/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/AutumnTrace/velocity-guard/releases/tag/v0.1.0
