using System.Text.Json.Serialization;
using Polyphony.Configuration;

namespace Polyphony;

[JsonSerializable(typeof(RouteResult))]
[JsonSerializable(typeof(ValidateResult))]
[JsonSerializable(typeof(HierarchyResult))]
[JsonSerializable(typeof(HierarchyResult[]))]
[JsonSerializable(typeof(HealthResult))]
[JsonSerializable(typeof(HealthCheckResult))]
[JsonSerializable(typeof(ConfigValidationResult))]
[JsonSerializable(typeof(ConfigValidationDiagnostic[]))]
[JsonSerializable(typeof(PlanDepthGuardResult))]
[JsonSerializable(typeof(PlanNextChildResult))]
[JsonSerializable(typeof(PlannableChild))]
[JsonSerializable(typeof(PlanLoadTypeResult))]
[JsonSerializable(typeof(PlanReviewResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class PolyphonyJsonContext : JsonSerializerContext;
