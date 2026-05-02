using System.Text.Json.Serialization;
using Polyphony.Configuration;

namespace Polyphony;

[JsonSerializable(typeof(RouteResult))]
[JsonSerializable(typeof(ValidateResult))]
[JsonSerializable(typeof(HierarchyResult))]
[JsonSerializable(typeof(HierarchyResult[]))]
[JsonSerializable(typeof(ConfigValidationResult))]
[JsonSerializable(typeof(ConfigValidationDiagnostic[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class PolyphonyJsonContext : JsonSerializerContext;
