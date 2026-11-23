using Microsoft.Extensions.Logging;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Core.Diagnostics;

/// <summary>
/// Source-generated log messages for the hot and the important paths. No message carries card data,
/// IP addresses or device ids: attempt ids and rule ids are enough to find everything else.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(1001, LogLevel.Information, "Loaded rule set {RulesVersion} with {RuleCount} active rules")]
    public static partial void RulesLoaded(ILogger logger, string rulesVersion, int ruleCount);

    [LoggerMessage(1002, LogLevel.Information, "Rules reloaded: {PreviousVersion} -> {RulesVersion} ({RuleCount} active rules)")]
    public static partial void RulesReloaded(ILogger logger, string previousVersion, string rulesVersion, int ruleCount);

    [LoggerMessage(1003, LogLevel.Error, "Rejected a rules reload, still running {RulesVersion}. Problems: {Failures}")]
    public static partial void RulesRejected(ILogger logger, string rulesVersion, string failures);

    [LoggerMessage(2001, LogLevel.Debug, "Assessed {AttemptId}: {Decision}, score {Score}, {Hits} hit(s), {ElapsedMs:0.000} ms")]
    public static partial void Assessed(ILogger logger, string attemptId, Decision decision, int score, int hits, double elapsedMs);

    [LoggerMessage(2002, LogLevel.Information, "Assessed {AttemptId} in degraded mode without {Unavailable}: rules said {RulesDecision}, policy {Policy} gave {Decision}")]
    public static partial void AssessedDegraded(ILogger logger, string attemptId, string unavailable, Decision rulesDecision, DegradedPolicy policy, Decision decision);

    [LoggerMessage(2003, LogLevel.Warning, "{Dependency} unavailable ({Reason}) while assessing {AttemptId}")]
    public static partial void DependencyUnavailable(ILogger logger, string dependency, string reason, string attemptId);

    /// <summary>While a circuit is open every request skips the dependency; the breaker itself logs the transition.</summary>
    [LoggerMessage(2005, LogLevel.Debug, "{Dependency} skipped while its circuit is open, assessing {AttemptId}")]
    public static partial void DependencyCircuitOpen(ILogger logger, string dependency, string attemptId);

    public static void Unavailable(ILogger logger, DependencyUnavailableException exception, string attemptId)
    {
        if (exception.Reason == "circuit-open")
        {
            DependencyCircuitOpen(logger, exception.Dependency, attemptId);
        }
        else
        {
            DependencyUnavailable(logger, exception.Dependency, exception.Reason, attemptId);
        }
    }

    [LoggerMessage(2004, LogLevel.Warning, "Could not release the tentative amount of {AttemptId}: {Reason}")]
    public static partial void ReleaseFailed(ILogger logger, string attemptId, string reason);

    [LoggerMessage(3001, LogLevel.Information, "Outcome {Outcome} recorded for {AttemptId}")]
    public static partial void OutcomeRecorded(ILogger logger, PaymentOutcome outcome, string attemptId);

    [LoggerMessage(3002, LogLevel.Information, "Chargeback on {AttemptId}: its {Dimension} was added to {List}")]
    public static partial void BlockedAfterChargeback(ILogger logger, string attemptId, Dimension dimension, string list);
}
