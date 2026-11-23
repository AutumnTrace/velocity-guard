using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace VelocityGuard.Api.Hosting;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Shared secret for the list admin endpoints, sent as <c>X-Admin-Key</c>. When empty, the admin API is disabled.
    /// In production this would sit behind real authentication; the key keeps a demo honest about the boundary.
    /// </summary>
    public string? AdminApiKey { get; set; }
}

/// <summary>Guards the admin endpoints with <c>X-Admin-Key</c>, compared in constant time.</summary>
public sealed class AdminKeyFilter(IOptionsMonitor<SecurityOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configured = options.CurrentValue.AdminApiKey;
        if (string.IsNullOrEmpty(configured))
        {
            return TypedResults.Problem(
                title: "Admin API disabled",
                detail: $"Set {SecurityOptions.SectionName}:AdminApiKey to enable the list endpoints.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!FixedTimeEquals(presented, configured))
        {
            return TypedResults.Problem(
                title: "Missing or invalid admin key",
                detail: $"Send the admin key in the {HeaderName} header.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }

    /// <summary>
    /// Hashes both sides first so the comparison takes the same time whatever the lengths,
    /// then compares the digests with <see cref="CryptographicOperations.FixedTimeEquals"/>.
    /// </summary>
    internal static bool FixedTimeEquals(string presented, string expected)
    {
        Span<byte> a = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> b = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), a);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), b);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
