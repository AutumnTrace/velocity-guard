using System.Diagnostics;
using System.Diagnostics.Metrics;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Core.Diagnostics;

/// <summary>
/// OpenTelemetry-friendly metrics for the risk engine (meter <c>VelocityGuard</c>).
/// Durations are in seconds, following the OpenTelemetry semantic conventions.
/// </summary>
public sealed class RiskMetrics
{
    public const string MeterName = "VelocityGuard";

    // Sub-millisecond buckets: the interesting part of this histogram is between 10 µs and 5 ms.
    private static readonly double[] LatencyBuckets =
        [0.00001, 0.000025, 0.00005, 0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25];

    private static readonly object BoxedTrue = true;
    private static readonly object BoxedFalse = false;

    private readonly Counter<long> _assessments;
    private readonly Counter<long> _ruleHits;
    private readonly Histogram<double> _assessmentDuration;
    private readonly Histogram<double> _evaluationDuration;
    private readonly Counter<long> _cacheLookups;
    private readonly Counter<long> _cacheMisses;
    private readonly Counter<long> _degraded;
    private readonly Counter<long> _ruleReloads;
    private readonly Counter<long> _outcomes;

    public RiskMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        var advice = new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets };

        _assessments = meter.CreateCounter<long>("velocityguard.assessments", "{assessment}", "Assessments by decision and degraded mode.");
        _ruleHits = meter.CreateCounter<long>("velocityguard.rule.hits", "{hit}", "Rule hits by rule id and mode (active or shadow).");
        _assessmentDuration = meter.CreateHistogram("velocityguard.assessment.duration", "s", "End-to-end assessment time: signals, velocity round trip, evaluation.", advice: advice);
        _evaluationDuration = meter.CreateHistogram("velocityguard.evaluation.duration", "s", "Rule evaluation time only.", advice: advice);
        _cacheLookups = meter.CreateCounter<long>("velocityguard.cache.lookups", "{lookup}", "Profile and list lookups through HybridCache.");
        _cacheMisses = meter.CreateCounter<long>("velocityguard.cache.misses", "{miss}", "Lookups that missed both cache levels and hit the system of record.");
        _degraded = meter.CreateCounter<long>("velocityguard.degraded", "{assessment}", "Assessments decided without one or more signals, by policy and dependency.");
        _ruleReloads = meter.CreateCounter<long>("velocityguard.rules.reloads", "{reload}", "Rule configuration reloads by result (applied or rejected).");
        _outcomes = meter.CreateCounter<long>("velocityguard.outcomes", "{outcome}", "Payment outcomes reported back by callers.");
    }

    public void AssessmentCompleted(Decision decision, bool degraded, TimeSpan elapsed)
    {
        var tags = new TagList { { "decision", DecisionTag(decision) }, { "degraded", degraded ? BoxedTrue : BoxedFalse } };
        _assessments.Add(1, tags);
        _assessmentDuration.Record(elapsed.TotalSeconds, tags);
    }

    public void Evaluated(TimeSpan elapsed) => _evaluationDuration.Record(elapsed.TotalSeconds);

    public void RuleHit(string ruleId, bool shadow) =>
        _ruleHits.Add(1, new KeyValuePair<string, object?>("rule", ruleId), new KeyValuePair<string, object?>("mode", shadow ? "shadow" : "active"));

    public void CacheLookup(string cache) => _cacheLookups.Add(1, new KeyValuePair<string, object?>("cache", cache));

    public void CacheMiss(string cache) => _cacheMisses.Add(1, new KeyValuePair<string, object?>("cache", cache));

    public void Degraded(DegradedPolicy policy, string dependency) =>
        _degraded.Add(1, new KeyValuePair<string, object?>("policy", PolicyTag(policy)), new KeyValuePair<string, object?>("dependency", dependency));

    public void RulesReloaded(bool applied) => _ruleReloads.Add(1, new KeyValuePair<string, object?>("result", applied ? "applied" : "rejected"));

    public void OutcomeRecorded(PaymentOutcome outcome) => _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", OutcomeTag(outcome)));

    private static string DecisionTag(Decision decision) => decision switch
    {
        Decision.Approve => "approve",
        Decision.Review => "review",
        _ => "decline",
    };

    private static string PolicyTag(DegradedPolicy policy) => policy switch
    {
        DegradedPolicy.FailOpen => "fail-open",
        DegradedPolicy.FailClosed => "fail-closed",
        _ => "review",
    };

    private static string OutcomeTag(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Approved => "approved",
        PaymentOutcome.Declined => "declined",
        _ => "chargeback",
    };
}

/// <summary>Tracing for the engine (activity source <c>VelocityGuard</c>).</summary>
public static class RiskTelemetry
{
    public const string SourceName = "VelocityGuard";

    public static readonly ActivitySource Source = new(SourceName);
}
