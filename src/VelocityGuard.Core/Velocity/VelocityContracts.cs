using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Core.Velocity;

/// <summary>Record one attempt in every window the plan needs and read the window values back.</summary>
public sealed record VelocityRequest(PaymentAttempt Attempt, VelocityPlan Plan, DateTimeOffset Now);

/// <summary>
/// Sliding-window counters. Implementations: <see cref="InMemoryVelocityStore"/> for local runs and tests,
/// and the Redis store (sorted sets updated by a Lua script) for real deployments.
/// </summary>
/// <remarks>
/// Windows are half-open: an event at <c>t</c> is inside a window of length <c>w</c> evaluated at <c>now</c>
/// when <c>now - w &lt; t</c>. Recording is idempotent per attempt id, so a retried assessment is not counted twice.
/// Implementations throw <see cref="DependencyUnavailableException"/> when the backing store cannot answer.
/// </remarks>
public interface IVelocityStore
{
    /// <summary>
    /// Adds the attempt to the count windows (and, tentatively, its amount to the sum windows) of every subject
    /// in the plan, and returns one value per plan slot. All of it costs one round trip to the store.
    /// Also journals the attempt so a later outcome can find it.
    /// </summary>
    ValueTask<long[]> RecordAsync(VelocityRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the attempt's amount back out of the sum windows because no money moved (we or the PSP declined it).
    /// Attempt counts are kept: a declined attempt is still an attempt. Idempotent.
    /// </summary>
    ValueTask ReleaseAmountAsync(AttemptRecord attempt, CancellationToken cancellationToken);
}

/// <summary>Short-lived record of assessed attempts, so outcomes reported later can be tied back to them.</summary>
public interface IAttemptJournal
{
    ValueTask<AttemptRecord?> FindAsync(string attemptId, CancellationToken cancellationToken);

    /// <summary>Atomically moves the attempt to <paramref name="outcome"/> if <see cref="OutcomeRules"/> allow it.</summary>
    ValueTask<OutcomeUpdate> SetOutcomeAsync(string attemptId, PaymentOutcome outcome, CancellationToken cancellationToken);
}

/// <summary>What the journal keeps about an attempt. No card data beyond the PSP token.</summary>
public sealed record AttemptRecord(
    string AttemptId,
    string PlayerId,
    string Currency,
    long AmountMinor,
    string? DeviceId,
    string IpKey,
    string? CardFingerprint,
    DateTimeOffset AssessedAt,
    PaymentOutcome? Outcome = null)
{
    public static AttemptRecord From(PaymentAttempt attempt, DateTimeOffset assessedAt) => new(
        attempt.AttemptId,
        attempt.PlayerId,
        attempt.Amount.Currency.Code,
        attempt.Amount.Minor,
        attempt.DeviceId,
        attempt.IpKey,
        attempt.CardFingerprint,
        assessedAt);

    public string? SubjectFor(Dimension dimension) => dimension switch
    {
        Dimension.Player => PlayerId,
        Dimension.Device => DeviceId,
        Dimension.Ip => IpKey,
        Dimension.Card => CardFingerprint,
        _ => null,
    };

    /// <summary>The sorted-set member for sum windows: unique per attempt, and carries currency and amount.</summary>
    public string AmountMember => AmountMemberFor(AttemptId, Currency, AmountMinor);

    public static string AmountMemberFor(string attemptId, string currency, long minor) => $"{attemptId}|{currency}|{minor}";
}

/// <summary>The final fate of a payment, as reported by the caller after the PSP answered.</summary>
public enum PaymentOutcome
{
    /// <summary>The PSP approved the deposit and money moved.</summary>
    Approved = 1,

    /// <summary>The PSP declined it, it failed, or it was cancelled. No money moved.</summary>
    Declined = 2,

    /// <summary>A previously approved deposit was charged back.</summary>
    Chargeback = 3,
}

public enum OutcomeUpdateStatus
{
    Applied,
    AlreadyRecorded,
    NotFound,
    InvalidTransition,
}

public sealed record OutcomeUpdate(OutcomeUpdateStatus Status, AttemptRecord? Record, PaymentOutcome? Previous);

/// <summary>Allowed outcome transitions: none to approved or declined, approved to chargeback.</summary>
public static class OutcomeRules
{
    public static bool IsAllowed(PaymentOutcome? from, PaymentOutcome to) => (from, to) switch
    {
        (null, PaymentOutcome.Approved or PaymentOutcome.Declined) => true,
        (PaymentOutcome.Approved, PaymentOutcome.Chargeback) => true,
        _ => false,
    };
}

/// <summary>Thrown by stores and repositories when their backing service is down, slow or circuit-broken.</summary>
public sealed class DependencyUnavailableException(string dependency, string reason, Exception? inner = null)
    : Exception($"{dependency} is unavailable: {reason}", inner)
{
    /// <summary>Short name, e.g. <c>velocity-store</c> or <c>risk-data</c>.</summary>
    public string Dependency { get; } = dependency;

    /// <summary>Short machine-friendly reason, e.g. <c>timeout</c> or <c>circuit-open</c>.</summary>
    public string Reason { get; } = reason;
}
