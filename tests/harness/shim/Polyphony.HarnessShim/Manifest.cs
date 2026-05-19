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

    /// <summary>
    /// Optional consumption cap. When set, this entry stops matching after
    /// it has been selected <c>Times</c> times in the current run (subsequent
    /// matchers, if any, get a chance to fire). When null, the entry is
    /// unlimited — the legacy first-match-wins behavior. Authors expressing
    /// per-call sequencing list the same (command, args) entry multiple
    /// times with <c>Times: 1</c> on each, in invocation order.
    /// </summary>
    [JsonPropertyName("times")]
    public int? Times { get; init; }
}

/// <summary>
/// Per-matcher-index consumption counter, persisted alongside the manifest
/// between shim invocations. Keys are decimal indices into
/// <see cref="Manifest.Responses"/>.
/// </summary>
internal sealed class CounterState
{
    [JsonPropertyName("counters")]
    public Dictionary<string, int> Counters { get; init; } = new();
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
[JsonSerializable(typeof(CounterState))]
[JsonSerializable(typeof(ShimError))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class ShimJsonContext : JsonSerializerContext;
