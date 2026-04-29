namespace Polyphony;

public sealed record RouteResult
{
    public required int WorkItemId { get; init; }
    public required string Phase { get; init; }
    public required string Action { get; init; }
    public string? Message { get; init; }
    public WorkspaceHint? WorkspaceHint { get; init; }
}

public sealed record WorkspaceHint
{
    public string? FeatureBranch { get; init; }
    public string? PgBranch { get; init; }
}
