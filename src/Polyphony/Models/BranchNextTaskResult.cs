namespace Polyphony;

/// <summary>
/// Result of <c>polyphony branch next-task</c>. Mirrors the JSON shape of
/// the legacy <c>scripts/task-router.ps1</c>.
/// </summary>
public sealed record BranchNextTaskResult
{
    public required string Action { get; init; }
    public required int PrimaryId { get; init; }
    public required string PrimaryTitle { get; init; }
    public required string PrimaryType { get; init; }
    public required int ContainerId { get; init; }
    public required string ContainerTitle { get; init; }
    public required string ContainerType { get; init; }
    public required int RemainingCount { get; init; }
    public required string CurrentPg { get; init; }
    public required string BranchName { get; init; }
    public required string AdoWorkspace { get; init; }
    public string? Error { get; init; }
}
