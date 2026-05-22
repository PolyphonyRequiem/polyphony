namespace Polyphony.Journal.Payloads;

/// <summary>
/// Payload schema for <c>branch_next_impl</c>: root routing input,
/// selected implementable item (if any), derived branch/state targets, and
/// whether the verb transitioned state, no-oped, or emitted an error route.
/// </summary>
public sealed record BranchNextImplPayload
{
    public required int RootId { get; init; }
    public int? SelectedWorkItemId { get; init; }
    public int? ContainerId { get; init; }
    public required string MergeGroupName { get; init; }
    public required string ResultAction { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }
    public required bool AlreadyInDesiredState { get; init; }
    public string? BranchName { get; init; }
    public string? TargetState { get; init; }
    public string? AdoWorkspace { get; init; }
    public string? Error { get; init; }
}
