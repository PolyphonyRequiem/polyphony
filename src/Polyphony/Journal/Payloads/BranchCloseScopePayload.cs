namespace Polyphony.Journal.Payloads;

public sealed record BranchCloseScopePayload
{
    public required int RootWorkItemId { get; init; }
    public required string MergeGroupName { get; init; }
    public required int PrNumber { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required IReadOnlyList<ClosedItem> ClosedItems { get; init; }
    public required IReadOnlyList<FailedClosure> FailedClosures { get; init; }
    public string? AdoWorkspace { get; init; }
    public string? Error { get; init; }
}
