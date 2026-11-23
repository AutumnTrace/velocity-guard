using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelocityGuard.Api.Contracts;

/// <summary>
/// Source-generated serialization metadata for the API contracts: no reflection on the request path.
/// Enums are written as camelCase strings (<c>"decision": "review"</c>).
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AssessmentRequest))]
[JsonSerializable(typeof(AssessmentResponse))]
[JsonSerializable(typeof(OutcomeRequest))]
[JsonSerializable(typeof(OutcomeResponse))]
[JsonSerializable(typeof(RulesResponse))]
[JsonSerializable(typeof(ListResponse))]
[JsonSerializable(typeof(ListEntryResponse))]
[JsonSerializable(typeof(ReplaceListRequest))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
