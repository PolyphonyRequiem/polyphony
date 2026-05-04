namespace Polyphony;

/// <summary>An item that was successfully transitioned to a terminal state.</summary>
public sealed record ClosedItem
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string TargetState { get; init; }
}

/// <summary>An item that could not be closed; the reason is the validator's message.</summary>
public sealed record FailedClosure
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Result of <c>polyphony branch close-scope</c>. Mirrors the JSON shape
/// emitted by the legacy <c>scripts/scope-closer.ps1</c>.
/// </summary>
public sealed record BranchCloseScopeResult
{
    public required string PgName { get; init; }
    public required int PrNumber { get; init; }
    public required IReadOnlyList<ClosedItem> ClosedItems { get; init; }
    public required IReadOnlyList<FailedClosure> FailedClosures { get; init; }
    public required int TotalClosed { get; init; }
    public required int TotalFailed { get; init; }
    public required string AdoWorkspace { get; init; }
    public string? Error { get; init; }
}
