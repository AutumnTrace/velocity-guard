using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Core.Signals;

/// <summary>
/// System of record for player profiles and risk lists. Reads go through <see cref="RiskSignalCache"/>;
/// writes go here first and then invalidate the cache.
/// </summary>
/// <remarks>Implementations throw <see cref="DependencyUnavailableException"/> when their backing store is down.</remarks>
public interface IRiskDataRepository
{
    ValueTask<PlayerProfile> GetProfileAsync(string playerId, CancellationToken cancellationToken);

    /// <summary>Folds an outcome into the player's profile and returns the new profile.</summary>
    ValueTask<PlayerProfile> ApplyOutcomeAsync(string playerId, PaymentOutcome outcome, DateTimeOffset at, CancellationToken cancellationToken);

    ValueTask<bool> IsListedAsync(string list, string value, CancellationToken cancellationToken);

    /// <summary>Returns true when the value was not on the list before.</summary>
    ValueTask<bool> AddToListAsync(string list, string value, CancellationToken cancellationToken);

    /// <summary>Returns true when the value was on the list.</summary>
    ValueTask<bool> RemoveFromListAsync(string list, string value, CancellationToken cancellationToken);

    /// <summary>Atomically replaces every entry of a list.</summary>
    ValueTask ReplaceListAsync(string list, IReadOnlyCollection<string> values, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> entries, sorted, for the admin API.</summary>
    ValueTask<IReadOnlyList<string>> GetListAsync(string list, int limit, CancellationToken cancellationToken);
}
