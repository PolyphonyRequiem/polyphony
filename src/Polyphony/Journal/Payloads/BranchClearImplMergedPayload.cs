namespace Polyphony.Journal.Payloads;

/// <summary>
/// Payload schema for <c>branch_clear_impl_merged</c>: target work item,
/// merge-group marker tag, mutation/no-op classification, and any routing
/// error surfaced by the marker verb.
/// </summary>
public sealed record BranchClearImplMergedPayload
{
    public required int WorkItemId { get; init; }
    public required string MergeGroupPath { get; init; }
    public required string Tag { get; init; }
    public required string Operation { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required bool AlreadyInDesiredState { get; init; }
    public string? Error { get; init; }
}
