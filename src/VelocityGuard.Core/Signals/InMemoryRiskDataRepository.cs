using System.Collections.Concurrent;
using System.Collections.Frozen;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Core.Signals;

/// <summary>Process-local profiles and lists, for running without Redis. Lists are copy-on-write frozen sets.</summary>
public sealed class InMemoryRiskDataRepository : IRiskDataRepository
{
    private readonly ConcurrentDictionary<string, PlayerProfile> _profiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FrozenSet<string>> _lists = new(StringComparer.Ordinal);

    public ValueTask<PlayerProfile> GetProfileAsync(string playerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_profiles.GetValueOrDefault(playerId, PlayerProfile.New));

    public ValueTask<PlayerProfile> ApplyOutcomeAsync(string playerId, PaymentOutcome outcome, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var updated = _profiles.AddOrUpdate(
            playerId,
            static (_, state) => Apply(PlayerProfile.New, state.outcome, state.at),
            static (_, current, state) => Apply(current, state.outcome, state.at),
            (outcome, at));
        return ValueTask.FromResult(updated);
    }

    public ValueTask<bool> IsListedAsync(string list, string value, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_lists.TryGetValue(list, out var entries) && entries.Contains(value));

    public ValueTask<bool> AddToListAsync(string list, string value, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Mutate(list, set => set.Add(value)));

    public ValueTask<bool> RemoveFromListAsync(string list, string value, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Mutate(list, set => set.Remove(value)));

    public ValueTask ReplaceListAsync(string list, IReadOnlyCollection<string> values, CancellationToken cancellationToken)
    {
        _lists[list] = values.ToFrozenSet(StringComparer.Ordinal);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> GetListAsync(string list, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> entries = _lists.TryGetValue(list, out var set)
            ? [.. set.Order(StringComparer.Ordinal).Take(limit)]
            : [];
        return ValueTask.FromResult(entries);
    }

    private static PlayerProfile Apply(PlayerProfile profile, PaymentOutcome outcome, DateTimeOffset at) => outcome switch
    {
        PaymentOutcome.Approved => profile with { ApprovedDeposits = profile.ApprovedDeposits + 1, LastApprovedAt = at },
        PaymentOutcome.Chargeback => profile with { Chargebacks = profile.Chargebacks + 1 },
        _ => profile,
    };

    private bool Mutate(string list, Func<HashSet<string>, bool> change)
    {
        while (true)
        {
            var current = _lists.GetOrAdd(list, static _ => FrozenSet<string>.Empty);
            var copy = new HashSet<string>(current, StringComparer.Ordinal);
            var changed = change(copy);
            if (!changed)
            {
                return false;
            }

            if (_lists.TryUpdate(list, copy.ToFrozenSet(StringComparer.Ordinal), current))
            {
                return true;
            }
        }
    }
}
