using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using VelocityGuard.Api.Contracts;
using VelocityGuard.Api.Hosting;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Signals;

namespace VelocityGuard.Api.Endpoints;

public static partial class RulesAndListEndpoints
{
    private const int MaxListEntries = 10_000;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ListName();

    public static IEndpointRouteBuilder MapRulesAndListEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/rules", (RuleSetProvider rules) => TypedResults.Ok(RulesResponse.From(rules.Current)))
            .WithTags("Rules")
            .WithName("GetRules")
            .WithSummary("The active, compiled rule set and its version");

        var lists = app.MapGroup("/v1/lists")
            .WithTags("Lists (admin)")
            .AddEndpointFilter<AdminKeyFilter>();

        lists.MapGet("/{list}", GetListAsync).WithName("GetList").WithSummary("Entries of a block or allow list");
        lists.MapPut("/{list}/entries/{value}", AddEntryAsync).WithName("AddListEntry").WithSummary("Add one entry");
        lists.MapDelete("/{list}/entries/{value}", RemoveEntryAsync).WithName("RemoveListEntry").WithSummary("Remove one entry");
        lists.MapPut("/{list}", ReplaceListAsync).WithName("ReplaceList").WithSummary("Replace every entry of a list");

        return app;
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem>> GetListAsync(
        string list,
        IRiskDataRepository repository,
        CancellationToken cancellationToken,
        int limit = 100)
    {
        if (Validate(list, value: null) is { } problem)
        {
            return problem;
        }

        var entries = await repository.GetListAsync(list, Math.Clamp(limit, 1, 1000), cancellationToken);
        return TypedResults.Ok(new ListResponse(list, entries.Count, entries));
    }

    internal static async Task<Results<Ok<ListEntryResponse>, ValidationProblem>> AddEntryAsync(
        string list,
        string value,
        IRiskDataRepository repository,
        RiskSignalCache cache,
        CancellationToken cancellationToken)
    {
        if (Validate(list, value) is { } problem)
        {
            return problem;
        }

        var added = await repository.AddToListAsync(list, value, cancellationToken);
        await cache.ListEntryChangedAsync(list, value, cancellationToken);
        return TypedResults.Ok(new ListEntryResponse(list, value, added));
    }

    internal static async Task<Results<Ok<ListEntryResponse>, ValidationProblem>> RemoveEntryAsync(
        string list,
        string value,
        IRiskDataRepository repository,
        RiskSignalCache cache,
        CancellationToken cancellationToken)
    {
        if (Validate(list, value) is { } problem)
        {
            return problem;
        }

        var removed = await repository.RemoveFromListAsync(list, value, cancellationToken);
        await cache.ListEntryChangedAsync(list, value, cancellationToken);
        return TypedResults.Ok(new ListEntryResponse(list, value, removed));
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem>> ReplaceListAsync(
        string list,
        ReplaceListRequest request,
        IRiskDataRepository repository,
        RiskSignalCache cache,
        CancellationToken cancellationToken)
    {
        if (Validate(list, value: null) is { } problem)
        {
            return problem;
        }

        var entries = request.Entries ?? [];
        if (entries.Count > MaxListEntries || entries.Any(e => !IsValidEntry(e)))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { ["entries"] = [$"at most {MaxListEntries} ids of 1-128 letters, digits, '-', '_', '.', ':' or '/' (no card numbers)"] });
        }

        var distinct = entries.Distinct(StringComparer.Ordinal).ToArray();
        await repository.ReplaceListAsync(list, distinct, cancellationToken);

        // Every cached answer for this list, positive or negative, is now stale: one tag invalidates them all.
        await cache.ListReplacedAsync(list, cancellationToken);
        return TypedResults.Ok(new ListResponse(list, distinct.Length, [.. distinct.Order(StringComparer.Ordinal).Take(100)]));
    }

    private static ValidationProblem? Validate(string list, string? value)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ListName().IsMatch(list))
        {
            errors["list"] = ["must be 1-64 lower-case letters, digits or '-'"];
        }

        if (value is not null && !IsValidEntry(value))
        {
            errors["value"] = ["must be 1-128 letters, digits, '-', '_', '.', ':' or '/' and must not be a card number"];
        }

        return errors.Count == 0 ? null : TypedResults.ValidationProblem(errors, title: "Invalid list request.");
    }

    /// <summary>List entries are ids (players, devices, card tokens, IPs or IPv6 /64 keys); never raw card numbers.</summary>
    private static bool IsValidEntry(string value) =>
        value.Length is > 0 and <= 128
        && !PaymentAttemptParser.LooksLikeCardNumber(value)
        && PaymentAttemptParser.IsValidId(value.Replace('/', '-'), 128);
}
