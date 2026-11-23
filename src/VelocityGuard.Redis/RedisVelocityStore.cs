using System.Globalization;
using StackExchange.Redis;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Redis;

/// <summary>
/// Sliding windows on Redis sorted sets. Each subject is updated and read by one atomic Lua script;
/// the scripts for all subjects plus the journal write go out as one pipelined batch, so an assessment
/// costs a single network round trip however many windows the rules need.
/// </summary>
public sealed class RedisVelocityStore(IConnectionMultiplexer redis, RedisKeys keys, TimeSpan journalRetention) : IVelocityStore, IAttemptJournal
{
    private static readonly RedisValue Count = "c";
    private static readonly RedisValue Sum = "s";

    public async ValueTask<long[]> RecordAsync(VelocityRequest request, CancellationToken cancellationToken)
    {
        var (attempt, plan, at) = request;
        var now = at.ToUnixTimeMilliseconds();
        var values = new long[plan.SlotCount];
        var amountMember = AttemptRecord.AmountMemberFor(attempt.AttemptId, attempt.Amount.Currency.Code, attempt.Amount.Minor);

        var db = redis.GetDatabase();
        var batch = db.CreateBatch();
        var pending = new List<(SubjectPlan Subject, Task<RedisResult> Result)>(plan.Subjects.Length);

        foreach (var subject in plan.Subjects)
        {
            if (attempt.SubjectFor(subject.Dimension) is not { } key)
            {
                continue;
            }

            var args = new RedisValue[5 + (subject.Slots.Length * 2)];
            args[0] = now;
            args[1] = (long)subject.Retention.TotalMilliseconds;
            args[2] = attempt.AttemptId;
            args[3] = subject.HasSums ? amountMember : RedisValue.EmptyString;
            args[4] = attempt.Amount.Currency.Code;
            for (var i = 0; i < subject.Slots.Length; i++)
            {
                var spec = plan.Windows[subject.Slots[i]];
                args[5 + (i * 2)] = (long)spec.Window.TotalMilliseconds;
                args[6 + (i * 2)] = spec.Aggregate == WindowAggregate.Count ? Count : Sum;
            }

            RedisKey[] scriptKeys = [keys.Attempts(subject.Dimension, key), keys.Amounts(subject.Dimension, key)];
            pending.Add((subject, batch.ScriptEvaluateAsync(RedisScripts.RecordAttempt, scriptKeys, args)));
        }

        // Journal the attempt in the same round trip, so outcomes reported later can find it.
        var journalKey = keys.Journal(attempt.AttemptId);
        var journalWrite = batch.HashSetAsync(journalKey, JournalFields(AttemptRecord.From(attempt, at)));
        var journalExpiry = batch.KeyExpireAsync(journalKey, journalRetention);

        batch.Execute();
        await Task.WhenAll(pending.Select(p => (Task)p.Result).Append(journalWrite).Append(journalExpiry))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var (subject, result) in pending)
        {
            var windowValues = (long[])(await result.ConfigureAwait(false))!;
            for (var i = 0; i < subject.Slots.Length; i++)
            {
                values[subject.Slots[i]] = windowValues[i];
            }
        }

        return values;
    }

    public ValueTask ReleaseAmountAsync(AttemptRecord attempt, CancellationToken cancellationToken)
    {
        // Fire and forget: the caller does not wait for Redis to acknowledge a release.
        var db = redis.GetDatabase();
        var member = attempt.AmountMember;
        foreach (var dimension in Enum.GetValues<Dimension>())
        {
            if (attempt.SubjectFor(dimension) is { } subject)
            {
                db.SortedSetRemove(keys.Amounts(dimension, subject), member, CommandFlags.FireAndForget);
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<AttemptRecord?> FindAsync(string attemptId, CancellationToken cancellationToken)
    {
        var fields = await redis.GetDatabase().HashGetAllAsync(keys.Journal(attemptId)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return fields.Length == 0 ? null : ParseJournal(attemptId, fields);
    }

    public async ValueTask<OutcomeUpdate> SetOutcomeAsync(string attemptId, PaymentOutcome outcome, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var reply = (RedisResult[])(await db.ScriptEvaluateAsync(
                RedisScripts.SetOutcome,
                [keys.Journal(attemptId)],
                [OutcomeName(outcome)])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false))!;

        var status = (int)reply[0];
        var previous = ParseOutcome((string?)reply[1]);
        if (status == 0)
        {
            return new OutcomeUpdate(OutcomeUpdateStatus.NotFound, null, null);
        }

        var record = await FindAsync(attemptId, cancellationToken).ConfigureAwait(false);
        return new OutcomeUpdate(
            status switch
            {
                1 => OutcomeUpdateStatus.Applied,
                2 => OutcomeUpdateStatus.AlreadyRecorded,
                _ => OutcomeUpdateStatus.InvalidTransition,
            },
            record,
            previous);
    }

    private static HashEntry[] JournalFields(AttemptRecord record) =>
    [
        new("player", record.PlayerId),
        new("currency", record.Currency),
        new("minor", record.AmountMinor),
        new("ip", record.IpKey),
        new("device", record.DeviceId ?? string.Empty),
        new("card", record.CardFingerprint ?? string.Empty),
        new("at", record.AssessedAt.ToUnixTimeMilliseconds()),
    ];

    private static AttemptRecord ParseJournal(string attemptId, HashEntry[] fields)
    {
        var map = fields.ToDictionary(f => f.Name.ToString(), f => f.Value.ToString(), StringComparer.Ordinal);
        return new AttemptRecord(
            attemptId,
            map["player"],
            map["currency"],
            long.Parse(map["minor"], CultureInfo.InvariantCulture),
            NullIfEmpty(map.GetValueOrDefault("device")),
            map["ip"],
            NullIfEmpty(map.GetValueOrDefault("card")),
            DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(map["at"], CultureInfo.InvariantCulture)),
            ParseOutcome(map.GetValueOrDefault("outcome")));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string OutcomeName(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Approved => "approved",
        PaymentOutcome.Declined => "declined",
        _ => "chargeback",
    };

    private static PaymentOutcome? ParseOutcome(string? value) => value switch
    {
        "approved" => PaymentOutcome.Approved,
        "declined" => PaymentOutcome.Declined,
        "chargeback" => PaymentOutcome.Chargeback,
        _ => null,
    };
}
