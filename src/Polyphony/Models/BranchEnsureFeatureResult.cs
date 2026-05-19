namespace Polyphony;

/// <summary>
/// Output of <c>polyphony branch ensure-feature</c>: confirms the feature
/// branch exists locally and on the remote, creating it idempotently if
/// necessary. The root workflow calls this once; sub-workflows receive the
/// branch name as an input and trust it.
/// </summary>
public sealed record BranchEnsureFeatureResult
{
    /// <summary>The feature branch name that was ensured.</summary>
    public required string Branch { get; init; }

    /// <summary>
    /// <c>created</c> | <c>existed</c> | <c>checked_out</c> |
    /// <c>exists_in_other_worktree</c> (AB#211: branch already lives in
    /// a sibling worktree under the parallel-fleet apex convention —
    /// the branch's existence is satisfied; the caller can route to
    /// <see cref="WorktreePath"/> if it needs to operate on the
    /// checkout). On error, <c>error</c>.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>Whether the branch already existed on the remote before this call.</summary>
    public required bool RemoteExisted { get; init; }

    /// <summary>Whether the branch was pushed to the remote by this call.</summary>
    public required bool Pushed { get; init; }

    /// <summary>The base branch/ref the feature branch was created from (only set when action=created).</summary>
    public string? CreatedFrom { get; init; }

    /// <summary>
    /// Absolute path of the sibling worktree currently holding the
    /// feature branch checkout. Only populated when
    /// <see cref="Action"/> == <c>exists_in_other_worktree</c> (AB#211).
    /// Workflow consumers can <c>cd</c> here when they need to operate
    /// on the live checkout rather than try to claim it in this cwd.
    /// </summary>
    public string? WorktreePath { get; init; }

    /// <summary>Non-empty when the operation partially or fully failed.</summary>
    public string? Error { get; init; }
}

