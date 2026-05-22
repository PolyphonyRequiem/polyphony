namespace Polyphony.Journal.Payloads;

/// <summary>
/// Payload schema for <c>branch_ensure_plan</c>: root/item/parent ids,
/// resolved branch/base refs, post-ensure SHA, and whether the ensure call
/// mutated git state or returned idempotently.
/// </summary>
public sealed record BranchEnsurePlanPayload
{
    public required int RootId { get; init; }
    public required int WorkItemId { get; init; }
    public int? ParentItemId { get; init; }
    public required bool IsRootPlan { get; init; }
    public required string BranchName { get; init; }
    public required string BaseBranch { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required bool WasCreated { get; init; }
    public required bool WasPushed { get; init; }
    public required bool BaseFetched { get; init; }
    public string? Sha { get; init; }
    public string? Error { get; init; }
}
