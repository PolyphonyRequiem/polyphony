using System.Text.Json.Serialization;

namespace Polyphony;

[JsonSerializable(typeof(RouteResult))]
[JsonSerializable(typeof(ValidateResult))]
[JsonSerializable(typeof(HierarchyResult))]
[JsonSerializable(typeof(HierarchyResult[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class PolyphonyJsonContext : JsonSerializerContext;
