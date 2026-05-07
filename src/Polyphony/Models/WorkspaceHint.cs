using System.Text.Json.Serialization;

namespace Polyphony;

/// <summary>
/// Branch-name hints emitted alongside routing decisions. JSON wire key
/// <c>pg_branch</c> is preserved via <see cref="JsonPropertyNameAttribute"/>
/// for legacy consumers.
/// </summary>
public sealed record WorkspaceHint
{
    public string? FeatureBranch { get; init; }

    [JsonPropertyName("pg_branch")]
    public string? MergeGroupBranch { get; init; }
}
