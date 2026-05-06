using System.Text.Json.Serialization;

namespace Polyphony;

public sealed record RouteResult
{
    public required int WorkItemId { get; init; }
    public required string Phase { get; init; }
    public required string Action { get; init; }
    public string? Message { get; init; }
    public WorkspaceHint? WorkspaceHint { get; init; }
}

/// <summary>
/// Branch-name hints emitted alongside routing decisions. JSON wire key
/// <c>pg_branch</c> is preserved via <see cref="JsonPropertyNameAttribute"/>
/// until the workflow rewire PR ships.
/// </summary>
public sealed record WorkspaceHint
{
    public string? FeatureBranch { get; init; }

    [JsonPropertyName("pg_branch")]
    public string? MergeGroupBranch { get; init; }
}
