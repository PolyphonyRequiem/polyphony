namespace Polyphony;

/// <summary>
/// Output of <c>polyphony branch ensure-evidence-branch</c>: confirms the
/// evidence branch exists locally and on the remote, materializing the base
/// branch (default <c>feature/{apex_id}</c>, overridable via
/// <c>--from-ref</c>) first if it exists only remotely. The verb is
/// idempotent — if the branch already exists nothing is created and no push
/// is performed.
/// </summary>
public sealed record BranchEnsureEvidenceResult
{
    /// <summary>The fully-qualified evidence branch name (e.g. <c>evidence/100-200</c> or the orphan form <c>evidence/200</c> when apex == item).</summary>
    public required string Branch { get; init; }

    /// <summary>The base branch the evidence branch was (or would be) created from. Equals <c>feature/{apex_id}</c> by default; overridden by <c>--from-ref</c>.</summary>
    public required string BaseBranch { get; init; }

    /// <summary><c>created</c> | <c>checked_out</c> | <c>error</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Whether the evidence branch already existed on the remote before this call.</summary>
    public required bool RemoteExisted { get; init; }

    /// <summary>Whether the evidence branch was pushed to the remote by this call.</summary>
    public required bool Pushed { get; init; }

    /// <summary>Whether the base branch existed on the remote before this call.</summary>
    public required bool BaseRemoteExisted { get; init; }

    /// <summary>Whether the base branch was fetched from the remote by this call.</summary>
    public required bool BaseFetched { get; init; }

    /// <summary>The base branch the evidence branch was created from (only set when action=created).</summary>
    public string? CreatedFrom { get; init; }

    /// <summary>The apex (root) work-item id used to compose the branch name and default base. Equals <see cref="ItemId"/> when no apex is supplied.</summary>
    public required int ApexId { get; init; }

    /// <summary>The work-item id the evidence is for.</summary>
    public required int ItemId { get; init; }

    /// <summary>True when the branch name uses the orphan form <c>evidence/{item}</c> (apex == item); false when the combined form <c>evidence/{apex}-{item}</c> is used.</summary>
    public required bool Orphan { get; init; }

    /// <summary>The <c>--from-ref</c> value supplied by the caller. Empty when the verb defaulted to <c>feature/{apex_id}</c>.</summary>
    public required string FromRef { get; init; }

    /// <summary>Non-empty when the operation partially or fully failed.</summary>
    public string? Error { get; init; }
}
