namespace Polyphony;

/// <summary>
/// Item that's blocking a dependency check from passing — a predecessor
/// link whose work item is not in a terminal state.
/// </summary>
public sealed record BlockingItem
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
}

/// <summary>
/// Result of <c>polyphony branch check-deps</c>. Mirrors the JSON shape
/// emitted by the legacy <c>scripts/dependency-check.ps1</c> so existing
/// workflow YAML refs continue to bind correctly.
/// </summary>
public sealed record BranchCheckDepsResult
{
    public required bool Blocked { get; init; }
    public required string Status { get; init; }
    public required int WorkItemId { get; init; }
    public required IReadOnlyList<BlockingItem> BlockingItems { get; init; }
    public required int ReadyCount { get; init; }
    public required int TotalCount { get; init; }
    public required string Message { get; init; }

    /// <summary>
    /// Set to true when an unrecoverable error (e.g. ADO unreachable) was
    /// encountered. Routing scripts should still emit a parseable result;
    /// callers can route on this flag for alerting.
    /// </summary>
    public bool? Error { get; init; }
}
