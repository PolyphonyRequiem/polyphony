using System.Text.Json.Serialization;

namespace Polyphony;

/// <summary>
/// Branch-name hints emitted alongside routing decisions.
/// </summary>
public sealed record WorkspaceHint
{
    public string? FeatureBranch { get; init; }

    [JsonPropertyName("merge_group_branch")]
    public string? MergeGroupBranch { get; init; }
}
