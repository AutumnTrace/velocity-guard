using Grpc.Core;
using VelocityGuard.Api.Grpc.V1;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using CoreDecision = VelocityGuard.Core.Evaluation.Decision;
using GrpcDecision = VelocityGuard.Api.Grpc.V1.Decision;
using GrpcMethod = VelocityGuard.Api.Grpc.V1.PaymentMethod;

namespace VelocityGuard.Api.Grpc;

/// <summary>
/// gRPC front door to the same <see cref="AssessmentService"/> the REST endpoint uses. Validation is shared too:
/// the request is mapped to <see cref="PaymentAttemptInput"/> and goes through <see cref="PaymentAttemptParser"/>.
/// </summary>
public sealed class RiskGrpcService(AssessmentService assessments, TimeProvider time) : RiskService.RiskServiceBase
{
    public override async Task<AssessResponse> Assess(AssessRequest request, ServerCallContext context)
    {
        if (!PaymentAttemptParser.TryParse(ToInput(request), time, out var attempt, out var errors))
        {
            var detail = string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"));
            throw new RpcException(new Status(StatusCode.InvalidArgument, detail));
        }

        var result = await assessments.AssessAsync(attempt, context.CancellationToken);
        return ToResponse(result);
    }

    internal static PaymentAttemptInput ToInput(AssessRequest request)
    {
        // Minor units on the wire, major units for the parser, which re-checks the currency's exponent.
        decimal amount = request.AmountMinor;
        if (Currency.TryParse(request.Currency, out var currency))
        {
            amount = Money.ToMajor(request.AmountMinor, currency);
        }

        return new PaymentAttemptInput
        {
            AttemptId = NullIfEmpty(request.AttemptId),
            PlayerId = request.PlayerId,
            Amount = amount,
            Currency = request.Currency,
            Method = request.Method switch
            {
                GrpcMethod.Card => "card",
                GrpcMethod.Wallet => "wallet",
                GrpcMethod.BankTransfer => "bankTransfer",
                _ => null,
            },
            Ip = request.Ip,
            IpCountry = request.IpCountry,
            CardBinCountry = NullIfEmpty(request.CardBinCountry),
            CardFingerprint = NullIfEmpty(request.CardFingerprint),
            WalletNetwork = NullIfEmpty(request.WalletNetwork),
            DeviceId = NullIfEmpty(request.DeviceId),
            AccountAgeDays = request.HasAccountAgeDays ? request.AccountAgeDays : null,
        };
    }

    internal static AssessResponse ToResponse(AssessmentResult result)
    {
        var response = new AssessResponse
        {
            AttemptId = result.AttemptId,
            Decision = Map(result.Decision),
            Score = result.Score,
            RulesVersion = result.RulesVersion,
            LatencyMs = Math.Round(result.Elapsed.TotalMilliseconds, 3),
        };

        response.Reasons.AddRange(result.Reasons.Select(r => new Reason
        {
            Rule = r.RuleId,
            Type = TypeName(r.Type),
            Score = r.Score,
            Message = r.Message,
        }));
        response.EvaluatedRules.AddRange(result.EvaluatedRules);

        if (result.Degraded is { } degraded)
        {
            response.Degraded = new Degraded
            {
                Policy = degraded.Policy.ToString(),
                RulesDecision = Map(degraded.RulesDecision),
            };
            response.Degraded.Unavailable.AddRange(degraded.Unavailable);
            response.Degraded.SkippedRules.AddRange(degraded.SkippedRules);
        }

        return response;
    }

    private static GrpcDecision Map(CoreDecision decision) => decision switch
    {
        CoreDecision.Approve => GrpcDecision.Approve,
        CoreDecision.Review => GrpcDecision.Review,
        _ => GrpcDecision.Decline,
    };

    private static string TypeName(RuleType type)
    {
        var name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
