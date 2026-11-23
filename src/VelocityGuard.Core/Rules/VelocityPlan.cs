using System.Collections.Immutable;
using VelocityGuard.Core.Payments;

namespace VelocityGuard.Core.Rules;

public enum WindowAggregate
{
    /// <summary>Number of attempts in the window.</summary>
    Count = 0,

    /// <summary>Sum of amounts in the window, in the attempt's currency, in minor units.</summary>
    Sum = 1,
}

/// <summary>One value the velocity store must produce: an aggregate over a sliding window for one dimension.</summary>
public readonly record struct WindowSpec(Dimension Dimension, TimeSpan Window, WindowAggregate Aggregate);

/// <summary>What the store has to do for one subject: which slots to fill and how long to keep its events.</summary>
public sealed record SubjectPlan(Dimension Dimension, TimeSpan Retention, ImmutableArray<int> Slots, bool HasSums);

/// <summary>
/// The velocity work implied by a rule set, worked out once at compile time. Every distinct
/// (dimension, window, aggregate) gets a slot; rules point at slots, and the store fills a
/// <c>long[]</c> of slot values in a single round trip.
/// </summary>
public sealed class VelocityPlan
{
    public static readonly VelocityPlan Empty = new([]);

    private VelocityPlan(ImmutableArray<WindowSpec> windows)
    {
        Windows = windows;
        Subjects =
        [
            .. windows
                .Select((spec, slot) => (spec, slot))
                .GroupBy(x => x.spec.Dimension)
                .OrderBy(g => g.Key)
                .Select(g => new SubjectPlan(
                    g.Key,
                    g.Max(x => x.spec.Window),
                    [.. g.Select(x => x.slot)],
                    g.Any(x => x.spec.Aggregate == WindowAggregate.Sum))),
        ];
    }

    /// <summary>Slot index to window. Slots are stable for the lifetime of a compiled rule set.</summary>
    public ImmutableArray<WindowSpec> Windows { get; }

    public ImmutableArray<SubjectPlan> Subjects { get; }

    public int SlotCount => Windows.Length;

    public bool IsEmpty => Windows.IsEmpty;

    public sealed class Builder
    {
        private readonly List<WindowSpec> _windows = [];

        /// <summary>Returns the slot for <paramref name="spec"/>, adding it if it is new.</summary>
        public int SlotFor(WindowSpec spec)
        {
            var slot = _windows.IndexOf(spec);
            if (slot >= 0)
            {
                return slot;
            }

            _windows.Add(spec);
            return _windows.Count - 1;
        }

        public VelocityPlan Build() => _windows.Count == 0 ? Empty : new VelocityPlan([.. _windows]);
    }
}
