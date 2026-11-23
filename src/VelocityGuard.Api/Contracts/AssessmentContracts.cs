using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Api.Contracts;

/// <summary>A deposit attempt to assess. Strings are validated by the service, so a typo gets a precise error.</summary>
/// <param name="AttemptId">Caller's id for this attempt; makes retries idempotent. Generated (UUIDv7) when omitted.</param>
/// <param name="PlayerId">The player's account id.</param>
/// <param name="Amount">Amount in major units, e.g. <c>25.00</c>.</param>
/// <param name="Currency">ISO 4217 code, e.g. <c>EUR</c>.</param>
/// <param name="Method"><c>card</c>, <c>wallet</c> or <c>bankTransfer</c>.</param>
/// <param name="Ip">Player's IP address (IPv4 or IPv6).</param>
/// <param name="IpCountry">ISO 3166-1 alpha-2 country of the IP.</param>
/// <param name="Card">Card details, for card payments. Never send a card number.</param>
/// <param name="Wallet">Wallet details, for wallet payments.</param>
/// <param name="DeviceId">Device fingerprint id, if the client collects one.</param>
/// <param name="AccountAgeDays">Days since the player registered.</param>
public sealed record AssessmentRequest(
    string? AttemptId,
    string? PlayerId,
    decimal? Amount,
    string? Currency,
    string? Method,
    string? Ip,
    string? IpCountry,
    CardDetails? Card,
    WalletDetails? Wallet,
    string? DeviceId,
    int? AccountAgeDays)
{
    public PaymentAttemptInput ToInput() => new()
    {
        AttemptId = AttemptId,
        PlayerId = PlayerId,
        Amount = Amount,
        Currency = Currency,
        Method = Method,
        Ip = Ip,
        IpCountry = IpCountry,
        CardBinCountry = Card?.BinCountry,
        CardFingerprint = Card?.Fingerprint,
        WalletNetwork = Wallet?.Network,
        DeviceId = DeviceId,
        AccountAgeDays = AccountAgeDays,
    };
}

/// <param name="BinCountry">Issuing country of the card's BIN.</param>
/// <param name="Fingerprint">PSP token or card fingerprint. Values shaped like a card number are rejected.</param>
public sealed record CardDetails(string? BinCountry, string? Fingerprint);

/// <param name="Network">Wallet network or scheme name.</param>
public sealed record WalletDetails(string? Network);

public sealed record AssessmentResponse(
    string AttemptId,
    Decision Decision,
    int Score,
    IReadOnlyList<ReasonResponse> Reasons,
    IReadOnlyList<string> EvaluatedRules,
    IReadOnlyList<ReasonResponse>? ShadowHits,
    DegradedResponse? Degraded,
    string RulesVersion,
    double LatencyMs)
{
    public static AssessmentResponse From(AssessmentResult result) => new(
        result.AttemptId,
        result.Decision,
        result.Score,
        [.. result.Reasons.Select(ReasonResponse.From)],
        result.EvaluatedRules,
        result.ShadowHits.Count == 0 ? null : [.. result.ShadowHits.Select(ReasonResponse.From)],
        result.Degraded is { } d ? new DegradedResponse(d.Policy, d.Unavailable, d.SkippedRules, d.RulesDecision) : null,
        result.RulesVersion,
        Math.Round(result.Elapsed.TotalMilliseconds, 3));
}

/// <param name="Action">Only present when the rule forces a review or a decline.</param>
public sealed record ReasonResponse(string Rule, RuleType Type, int Score, RuleAction? Action, string Message)
{
    public static ReasonResponse From(RuleHit hit) =>
        new(hit.RuleId, hit.Type, hit.Score, hit.Action == RuleAction.Score ? null : hit.Action, hit.Message);
}

public sealed record DegradedResponse(
    DegradedPolicy Policy,
    IReadOnlyList<string> Unavailable,
    IReadOnlyList<string> SkippedRules,
    Decision RulesDecision);

/// <param name="Outcome"><c>approved</c>, <c>declined</c> or <c>chargeback</c>.</param>
public sealed record OutcomeRequest(string? Outcome);

public sealed record OutcomeResponse(
    string AttemptId,
    PaymentOutcome Outcome,
    PaymentOutcome? Previous,
    OutcomeStatus Status,
    ProfileResponse? Profile,
    IReadOnlyList<string>? BlockedOn)
{
    public static OutcomeResponse From(OutcomeResult result) => new(
        result.AttemptId,
        result.Outcome,
        result.Previous,
        result.Status,
        result.Profile is { } p ? ProfileResponse.From(p) : null,
        result.BlockedOn.Count == 0 ? null : result.BlockedOn);
}

public sealed record ProfileResponse(int ApprovedDeposits, int Chargebacks, DateTimeOffset? LastApprovedAt)
{
    public static ProfileResponse From(PlayerProfile profile) => new(profile.ApprovedDeposits, profile.Chargebacks, profile.LastApprovedAt);
}
