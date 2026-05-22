namespace Polyphony.Journal.Payloads;

/// <summary>
/// Payload schema for <c>branch_ensure_feature</c>: inferred root id when
/// available, resolved branch/base refs, post-ensure SHA, and whether the
/// invocation changed git state or was already satisfied.
/// </summary>
public sealed record BranchEnsureFeaturePayload
{
    public int? RootId { get; init; }
    public int? WorkItemId { get; init; }
    public required string BranchName { get; init; }
    public required string BaseBranch { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required bool WasCreated { get; init; }
    public required bool WasPushed { get; init; }
    public string? WorktreePath { get; init; }
    public string? Sha { get; init; }
    public string? Error { get; init; }
}
