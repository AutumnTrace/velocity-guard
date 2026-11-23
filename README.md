# 🛡️ VelocityGuard

**Real-time risk and velocity checks for payments: Redis sliding windows, a two-level HybridCache and a rules engine that answers in well under a millisecond.**

> Work in progress. The rules engine core is in place; the HTTP API, Redis store and benchmarks are on the way.

## Build and test

```bash
dotnet build
dotnet test --filter "Category!=Integration"
```

## License

[MIT](LICENSE) © Mina Rotari
