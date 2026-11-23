using Microsoft.AspNetCore.Http.HttpResults;
using VelocityGuard.Api.Contracts;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Api.Endpoints;

public static class AssessmentEndpoints
{
    public static IEndpointRouteBuilder MapAssessmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/assessments").WithTags("Assessments");

        group.MapPost(string.Empty, AssessAsync)
            .WithName("Assess")
            .WithSummary("Assess a deposit attempt")
            .WithDescription("Records the attempt in every velocity window, evaluates the active rules and returns approve, review or decline.");

        group.MapPost("/{attemptId}/outcome", RecordOutcomeAsync)
            .WithName("RecordOutcome")
            .WithSummary("Report what finally happened to an assessed payment")
            .WithDescription("approved builds the player's history; declined releases the amount from sum windows; chargeback marks the player and blocks the card and device.");

        return app;
    }

    internal static async Task<Results<Ok<AssessmentResponse>, ValidationProblem>> AssessAsync(
        AssessmentRequest request,
        AssessmentService service,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!PaymentAttemptParser.TryParse(request.ToInput(), time, out var attempt, out var errors))
        {
            return TypedResults.ValidationProblem(errors, title: "The payment attempt is invalid.");
        }

        var result = await service.AssessAsync(attempt, cancellationToken);
        return TypedResults.Ok(AssessmentResponse.From(result));
    }

    internal static async Task<Results<Ok<OutcomeResponse>, ValidationProblem, ProblemHttpResult>> RecordOutcomeAsync(
        string attemptId,
        OutcomeRequest request,
        OutcomeService service,
        CancellationToken cancellationToken)
    {
        var outcome = request.Outcome?.ToLowerInvariant() switch
        {
            "approved" => PaymentOutcome.Approved,
            "declined" => PaymentOutcome.Declined,
            "chargeback" => PaymentOutcome.Chargeback,
            _ => (PaymentOutcome?)null,
        };

        if (outcome is null || !PaymentAttemptParser.IsValidId(attemptId, PaymentAttemptParser.MaxIdLength))
        {
            var errors = new Dictionary<string, string[]>();
            if (outcome is null)
            {
                errors["outcome"] = ["must be one of: approved, declined, chargeback"];
            }

            if (!PaymentAttemptParser.IsValidId(attemptId, PaymentAttemptParser.MaxIdLength))
            {
                errors["attemptId"] = ["is not a valid attempt id"];
            }

            return TypedResults.ValidationProblem(errors, title: "The outcome is invalid.");
        }

        var result = await service.RecordAsync(attemptId, outcome.Value, cancellationToken);
        return result.Status switch
        {
            OutcomeStatus.NotFound => TypedResults.Problem(
                title: "Unknown attempt",
                detail: "No assessment with this id is on record. It was never assessed, or it is older than the journal retention.",
                statusCode: StatusCodes.Status404NotFound),
            OutcomeStatus.Conflict => TypedResults.Problem(
                title: "Outcome not allowed",
                detail: $"An attempt that is '{Describe(result.Previous)}' cannot become '{Describe(result.Outcome)}'. Allowed: none to approved or declined, approved to chargeback.",
                statusCode: StatusCodes.Status409Conflict),
            _ => TypedResults.Ok(OutcomeResponse.From(result)),
        };
    }

    private static string Describe(PaymentOutcome? outcome) => outcome?.ToString().ToLowerInvariant() ?? "pending";
}
