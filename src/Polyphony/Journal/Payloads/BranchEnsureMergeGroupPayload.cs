namespace Polyphony.Journal.Payloads;

/// <summary>
/// Payload schema for <c>branch_ensure_merge_group</c>: root id,
/// merge-group path/depth, resolved branch/base refs, post-ensure SHA, and
/// whether the invocation actually changed git state.
/// </summary>
public sealed record BranchEnsureMergeGroupPayload
{
    public required int RootId { get; init; }
    public required string MergeGroupPath { get; init; }
    public required int Depth { get; init; }
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
