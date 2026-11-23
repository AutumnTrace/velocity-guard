using System.Collections.Concurrent;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Core.Velocity;

/// <summary>
/// In-process sliding windows with exactly the semantics of the Redis store: half-open windows,
/// trimming by the subject's longest window, idempotent recording per attempt id.
/// Good for <c>dotnet run</c> without Docker, for tests, and as a reference model. Not shared across instances.
/// </summary>
public sealed class InMemoryVelocityStore : IVelocityStore, IAttemptJournal
{
    private const int SweepEvery = 4096;

    private readonly ConcurrentDictionary<(Dimension, string), SubjectEvents> _subjects = new();
    private readonly ConcurrentDictionary<string, JournalEntry> _journal = new(StringComparer.Ordinal);
    private readonly long _journalRetentionMs;
    private long _operations;

    public InMemoryVelocityStore(TimeSpan? journalRetention = null)
    {
        _journalRetentionMs = (long)(journalRetention ?? TimeSpan.FromDays(30)).TotalMilliseconds;
    }

    /// <summary>Number of subjects currently tracked (for tests and diagnostics).</summary>
    public int SubjectCount => _subjects.Count;

    public ValueTask<long[]> RecordAsync(VelocityRequest request, CancellationToken cancellationToken)
    {
        var (attempt, plan, at) = request;
        var now = at.ToUnixTimeMilliseconds();
        var values = new long[plan.SlotCount];
        var amount = new AmountEntry(now, attempt.AttemptId, attempt.Amount.Currency.Code, attempt.Amount.Minor);

        foreach (var subject in plan.Subjects)
        {
            if (attempt.SubjectFor(subject.Dimension) is not { } key)
            {
                continue;
            }

            // A subject swept concurrently refuses the write; fetch the fresh instance and retry.
            while (!_subjects.GetOrAdd((subject.Dimension, key), static _ => new SubjectEvents())
                       .TryRecord(now, subject, plan, amount, values))
            {
            }
        }

        _journal.TryAdd(attempt.AttemptId, new JournalEntry(AttemptRecord.From(attempt, at), now));

        if (Interlocked.Increment(ref _operations) % SweepEvery == 0)
        {
            Sweep(now);
        }

        return ValueTask.FromResult(values);
    }

    public ValueTask ReleaseAmountAsync(AttemptRecord attempt, CancellationToken cancellationToken)
    {
        foreach (var dimension in Enum.GetValues<Dimension>())
        {
            if (attempt.SubjectFor(dimension) is { } key && _subjects.TryGetValue((dimension, key), out var events))
            {
                events.ReleaseAmount(attempt.AttemptId);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<AttemptRecord?> FindAsync(string attemptId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_journal.TryGetValue(attemptId, out var entry) ? entry.Record : null);

    public ValueTask<OutcomeUpdate> SetOutcomeAsync(string attemptId, PaymentOutcome outcome, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!_journal.TryGetValue(attemptId, out var entry))
            {
                return ValueTask.FromResult(new OutcomeUpdate(OutcomeUpdateStatus.NotFound, null, null));
            }

            var previous = entry.Record.Outcome;
            if (previous == outcome)
            {
                return ValueTask.FromResult(new OutcomeUpdate(OutcomeUpdateStatus.AlreadyRecorded, entry.Record, previous));
            }

            if (!OutcomeRules.IsAllowed(previous, outcome))
            {
                return ValueTask.FromResult(new OutcomeUpdate(OutcomeUpdateStatus.InvalidTransition, entry.Record, previous));
            }

            var updated = entry with { Record = entry.Record with { Outcome = outcome } };
            if (_journal.TryUpdate(attemptId, updated, entry))
            {
                return ValueTask.FromResult(new OutcomeUpdate(OutcomeUpdateStatus.Applied, updated.Record, previous));
            }
        }
    }

    /// <summary>Drops subjects with no event inside their retention and journal entries past theirs.</summary>
    public void Sweep(long nowMs)
    {
        foreach (var (key, events) in _subjects)
        {
            if (events.TryRetire(nowMs))
            {
                _subjects.TryRemove(new KeyValuePair<(Dimension, string), SubjectEvents>(key, events));
            }
        }

        foreach (var (id, entry) in _journal)
        {
            if (entry.RecordedAtMs <= nowMs - _journalRetentionMs)
            {
                _journal.TryRemove(new KeyValuePair<string, JournalEntry>(id, entry));
            }
        }
    }

    private sealed record JournalEntry(AttemptRecord Record, long RecordedAtMs);

    private readonly record struct AttemptEntry(long At, string AttemptId);

    private readonly record struct AmountEntry(long At, string AttemptId, string Currency, long Minor);

    /// <summary>One subject's events, kept sorted by time. Every operation runs under the instance lock.</summary>
    private sealed class SubjectEvents
    {
        private readonly Lock _gate = new();
        private readonly List<AttemptEntry> _attempts = [];
        private readonly HashSet<string> _attemptIds = new(StringComparer.Ordinal);
        private readonly List<AmountEntry> _amounts = [];
        private readonly HashSet<string> _amountIds = new(StringComparer.Ordinal);
        private long _latestMs;
        private long _retentionMs;
        private bool _retired;

        public bool TryRecord(long now, SubjectPlan subject, VelocityPlan plan, AmountEntry amount, long[] values)
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _retentionMs = (long)subject.Retention.TotalMilliseconds;
                var floor = now - _retentionMs;

                TrimAttempts(floor);
                if (_attemptIds.Add(amount.AttemptId))
                {
                    _attempts.Insert(UpperBound(_attempts, now), new AttemptEntry(now, amount.AttemptId));
                }

                if (subject.HasSums)
                {
                    TrimAmounts(floor);
                    if (_amountIds.Add(amount.AttemptId))
                    {
                        _amounts.Insert(UpperBound(_amounts, now), amount);
                    }
                }

                _latestMs = Math.Max(_latestMs, now);

                foreach (var slot in subject.Slots)
                {
                    var spec = plan.Windows[slot];
                    var from = now - (long)spec.Window.TotalMilliseconds;
                    values[slot] = spec.Aggregate == WindowAggregate.Count
                        ? _attempts.Count - UpperBound(_attempts, from)
                        : SumAfter(from, amount.Currency);
                }

                return true;
            }
        }

        public void ReleaseAmount(string attemptId)
        {
            lock (_gate)
            {
                if (_amountIds.Remove(attemptId))
                {
                    _amounts.RemoveAt(_amounts.FindIndex(a => a.AttemptId == attemptId));
                }
            }
        }

        public bool TryRetire(long now)
        {
            lock (_gate)
            {
                if (_latestMs > now - _retentionMs)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }

        private long SumAfter(long from, string currency)
        {
            var sum = 0L;
            for (var i = UpperBound(_amounts, from); i < _amounts.Count; i++)
            {
                if (_amounts[i].Currency == currency)
                {
                    sum += _amounts[i].Minor;
                }
            }

            return sum;
        }

        private void TrimAttempts(long floor)
        {
            var stale = UpperBound(_attempts, floor);
            for (var i = 0; i < stale; i++)
            {
                _attemptIds.Remove(_attempts[i].AttemptId);
            }

            _attempts.RemoveRange(0, stale);
        }

        private void TrimAmounts(long floor)
        {
            var stale = UpperBound(_amounts, floor);
            for (var i = 0; i < stale; i++)
            {
                _amountIds.Remove(_amounts[i].AttemptId);
            }

            _amounts.RemoveRange(0, stale);
        }

        /// <summary>Index of the first entry with a timestamp strictly greater than <paramref name="at"/>.</summary>
        private static int UpperBound(List<AttemptEntry> list, long at)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (list[mid].At <= at)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        private static int UpperBound(List<AmountEntry> list, long at)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (list[mid].At <= at)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }
}
