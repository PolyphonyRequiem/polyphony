using System.Text.Json.Serialization;

namespace Polyphony.HarnessShim;

internal sealed class Manifest
{
    [JsonPropertyName("responses")]
    public List<ManifestResponse> Responses { get; init; } = new();

    [JsonPropertyName("audit_log")]
    public string? AuditLog { get; init; }
}

internal sealed class ManifestResponse
{
    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    [JsonPropertyName("args")]
    public List<string>? Args { get; init; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; init; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; init; }

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; init; }
}

internal sealed class ShimError
{
    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    [JsonPropertyName("argv")]
    public string[] Argv { get; init; } = Array.Empty<string>();
}

[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(ManifestResponse))]
[JsonSerializable(typeof(ShimError))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class ShimJsonContext : JsonSerializerContext;
