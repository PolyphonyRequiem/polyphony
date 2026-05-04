namespace Polyphony;

/// <summary>
/// Result of <c>polyphony branch next-task</c>. Mirrors the JSON shape of
/// the legacy <c>scripts/task-router.ps1</c>.
/// </summary>
public sealed record BranchNextTaskResult
{
    public required string Action { get; init; }
    public required int TaskId { get; init; }
    public required string TaskTitle { get; init; }
    public required int IssueId { get; init; }
    public required string IssueTitle { get; init; }
    public required int RemainingCount { get; init; }
    public required string CurrentPg { get; init; }
    public required string BranchName { get; init; }
    public required string AdoWorkspace { get; init; }
    public string? Error { get; init; }
}
