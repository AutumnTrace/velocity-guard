using Microsoft.AspNetCore.Diagnostics;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Api.Hosting;

/// <summary>
/// Assessments degrade gracefully, but writes (outcomes, list changes) cannot be faked: when their store is
/// down the caller gets <c>503 Service Unavailable</c> with a <c>Retry-After</c>, not a 500.
/// </summary>
internal sealed class DependencyUnavailableHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DependencyUnavailableException unavailable)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "5";
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Title = "Dependency unavailable",
                Detail = $"{unavailable.Dependency} is unavailable ({unavailable.Reason}). Retry shortly.",
                Status = StatusCodes.Status503ServiceUnavailable,
            },
        });
    }
}
