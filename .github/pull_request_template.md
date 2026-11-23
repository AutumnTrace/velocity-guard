## Summary

<!-- What does this change and why? -->

## Changes

-

## Testing

- [ ] `dotnet test --filter "Category!=Integration"` passes locally
- [ ] Integration tests (Testcontainers) updated if Redis behaviour changed; CI runs them
- [ ] Rule changes: `rules.json` still validates, and the README examples still hold
- [ ] Performance-sensitive change: benchmarks re-run and the README numbers updated
- [ ] `CHANGELOG.md` updated under **Unreleased**
