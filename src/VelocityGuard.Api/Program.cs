using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using VelocityGuard.Api.Contracts;
using VelocityGuard.Api.Endpoints;
using VelocityGuard.Api.Grpc;
using VelocityGuard.Api.Hosting;
using VelocityGuard.Core.Assessment;

var builder = WebApplication.CreateBuilder(args);

// Rules live in their own file and hot-reload: edit rules.json and the next request uses the new set.
builder.Configuration.AddJsonFile(
    builder.Configuration["Risk:RulesFile"] ?? "rules.json",
    optional: false,
    reloadOnChange: true);

builder.AddVelocityGuardObservability();
builder.Services.AddVelocityGuard(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// RFC 9457 problem details for every error, including a traceId to find the request in traces and logs.
builder.Services.AddProblemDetails();

// Malformed JSON is the caller's fault: answer 400 with problem details in every environment
// (minimal APIs otherwise throw in Development, which surfaces as a 500).
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

builder.Services.AddOpenApi();
builder.Services.AddGrpc(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());

var app = builder.Build();

// Compile the rules now: an invalid rules.json stops startup instead of failing the first request.
_ = app.Services.GetRequiredService<RuleSetProvider>();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options => options.GetLevel = (http, _, ex) =>
    ex is not null || http.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : http.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
    : Serilog.Events.LogEventLevel.Information);

app.MapOpenApi();
app.MapAssessmentEndpoints();
app.MapRulesAndListEndpoints();
app.MapGrpcService<RiskGrpcService>();
app.MapVelocityGuardHealthChecks();

app.Run();

/// <summary>Entry point, exposed for <c>WebApplicationFactory</c> in the tests.</summary>
public partial class Program;
