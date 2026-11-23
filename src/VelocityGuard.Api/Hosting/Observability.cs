using System.Text.Json;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Diagnostics;

namespace VelocityGuard.Api.Hosting;

public static class Observability
{
    public const string ServiceName = "velocity-guard";

    /// <summary>
    /// Serilog for logs; OpenTelemetry for traces and metrics. OTLP export switches on when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, Azure Monitor when <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is.
    /// </summary>
    public static WebApplicationBuilder AddVelocityGuardObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", ServiceName));

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                ServiceName,
                serviceVersion: typeof(Observability).Assembly.GetName().Version?.ToString(3)))
            .WithMetrics(metrics => metrics
                .AddMeter(RiskMetrics.MeterName, "Polly")
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(RiskTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            otel.UseAzureMonitor();
        }

        builder.Services.AddHealthChecks()
            .AddCheck<RulesHealthCheck>("rules", tags: ["ready"]);

        return builder;
    }

    /// <summary>Liveness never checks dependencies (a Redis blip must not restart pods); readiness does.</summary>
    public static WebApplication MapVelocityGuardHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new() { Predicate = _ => false, ResponseWriter = WriteAsync });
        app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = WriteAsync });
        return app;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 2),
            }),
        });
    }
}

/// <summary>Ready once a compiled rule set is active; reports its version.</summary>
internal sealed class RulesHealthCheck(RuleSetProvider rules) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var current = rules.Current;
        return Task.FromResult(HealthCheckResult.Healthy($"Rule set {current.Version}, {current.Count} active rules"));
    }
}
