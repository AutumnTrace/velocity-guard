using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VelocityGuard.Core.Diagnostics;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Core.Assessment;

public sealed class OutcomeOptions
{
    public const string SectionName = "Outcomes";

    /// <summary>Add the attempt's identifiers to deny lists when a chargeback is reported.</summary>
    public bool BlockOnChargeback { get; set; } = true;

    /// <summary>Which identifier goes on which list after a chargeback.</summary>
    public Dictionary<Dimension, string> ChargebackBlocklists { get; set; } = new()
    {
        [Dimension.Card] = "blocked-cards",
        [Dimension.Device] = "blocked-devices",
    };
}

public enum OutcomeStatus
{
    Recorded,
    AlreadyRecorded,
    NotFound,
    Conflict,
}

public sealed record OutcomeResult(
    OutcomeStatus Status,
    string AttemptId,
    PaymentOutcome Outcome,
    PaymentOutcome? Previous,
    PlayerProfile? Profile,
    IReadOnlyList<string> BlockedOn);

/// <summary>
/// Feeds the real fate of a payment back into the signals: approved deposits build the player's history,
/// declines take the amount out of the sum windows, chargebacks mark the player and block the card and device.
/// </summary>
public sealed class OutcomeService(
    IAttemptJournal journal,
    IVelocityStore velocity,
    IRiskDataRepository repository,
    RiskSignalCache signals,
    IOptionsMonitor<OutcomeOptions> options,
    RiskMetrics metrics,
    TimeProvider time,
    ILogger<OutcomeService> logger)
{
    public async ValueTask<OutcomeResult> RecordAsync(string attemptId, PaymentOutcome outcome, CancellationToken cancellationToken)
    {
        var update = await journal.SetOutcomeAsync(attemptId, outcome, cancellationToken).ConfigureAwait(false);
        switch (update.Status)
        {
            case OutcomeUpdateStatus.NotFound:
                return new OutcomeResult(OutcomeStatus.NotFound, attemptId, outcome, null, null, []);
            case OutcomeUpdateStatus.InvalidTransition:
                return new OutcomeResult(OutcomeStatus.Conflict, attemptId, outcome, update.Previous, null, []);
            case OutcomeUpdateStatus.AlreadyRecorded:
                return new OutcomeResult(OutcomeStatus.AlreadyRecorded, attemptId, outcome, update.Previous, null, []);
        }

        var record = update.Record!;
        PlayerProfile? profile = null;
        List<string> blocked = [];

        switch (outcome)
        {
            case PaymentOutcome.Declined:
                await velocity.ReleaseAmountAsync(record, cancellationToken).ConfigureAwait(false);
                break;

            case PaymentOutcome.Approved:
                profile = await repository.ApplyOutcomeAsync(record.PlayerId, outcome, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                await signals.ProfileChangedAsync(record.PlayerId, cancellationToken).ConfigureAwait(false);
                break;

            case PaymentOutcome.Chargeback:
                profile = await repository.ApplyOutcomeAsync(record.PlayerId, outcome, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                await signals.ProfileChangedAsync(record.PlayerId, cancellationToken).ConfigureAwait(false);
                await BlockAsync(record, blocked, cancellationToken).ConfigureAwait(false);
                break;
        }

        metrics.OutcomeRecorded(outcome);
        Log.OutcomeRecorded(logger, outcome, attemptId);
        return new OutcomeResult(OutcomeStatus.Recorded, attemptId, outcome, update.Previous, profile, blocked);
    }

    private async ValueTask BlockAsync(AttemptRecord record, List<string> blocked, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!current.BlockOnChargeback)
        {
            return;
        }

        foreach (var (dimension, list) in current.ChargebackBlocklists)
        {
            if (record.SubjectFor(dimension) is not { } value)
            {
                continue;
            }

            await repository.AddToListAsync(list, value, cancellationToken).ConfigureAwait(false);
            await signals.ListEntryChangedAsync(list, value, cancellationToken).ConfigureAwait(false);
            blocked.Add(list);
            Log.BlockedAfterChargeback(logger, record.AttemptId, dimension, list);
        }
    }
}
