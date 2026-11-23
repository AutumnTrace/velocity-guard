using System.ComponentModel;

namespace VelocityGuard.Core.Signals;

/// <summary>
/// What we know about a player's payment history, built from recorded outcomes.
/// </summary>
/// <remarks>
/// Sealed and marked immutable so HybridCache can hand out the same instance on every L1 hit
/// instead of deserialising a fresh copy.
/// </remarks>
[ImmutableObject(true)]
public sealed record PlayerProfile(int ApprovedDeposits, int Chargebacks, DateTimeOffset? LastApprovedAt)
{
    public static readonly PlayerProfile New = new(0, 0, null);

    public bool HasDeposited => ApprovedDeposits > 0;
}
