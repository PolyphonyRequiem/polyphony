namespace Polyphony;

/// <summary>
/// Output of <c>polyphony branch ensure-mg</c>: confirms the merge-group
/// branch exists locally and on the remote, materializing the parent base
/// branch first if it exists only remotely. The verb is idempotent — if
/// the branch already exists nothing is created and no push is performed.
/// </summary>
public sealed record BranchEnsureMergeGroupResult
{
    /// <summary>The fully-qualified merge-group branch name (e.g. <c>mg/123_core</c>).</summary>
    public required string Branch { get; init; }

    /// <summary>The base branch the MG branch was (or would be) created from.</summary>
    public required string BaseBranch { get; init; }

    /// <summary><c>created</c> | <c>checked_out</c> | <c>error</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Whether the MG branch already existed on the remote before this call.</summary>
    public required bool RemoteExisted { get; init; }

    /// <summary>Whether the MG branch was pushed to the remote by this call.</summary>
    public required bool Pushed { get; init; }

    /// <summary>Whether the base branch existed on the remote before this call.</summary>
    public required bool BaseRemoteExisted { get; init; }

    /// <summary>Whether the base branch was fetched from the remote by this call.</summary>
    public required bool BaseFetched { get; init; }

    /// <summary>The base branch/ref the MG branch was created from (only set when action=created).</summary>
    public string? CreatedFrom { get; init; }

    /// <summary>The root work-item id supplied as input.</summary>
    public required int RootId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path supplied as input.</summary>
    public required string MgPath { get; init; }

    /// <summary>The depth of the merge-group path (1 = top-level).</summary>
    public required int Depth { get; init; }

    /// <summary>True when the path depth equals or exceeds the warning threshold (3).</summary>
    public required bool DepthWarning { get; init; }

    /// <summary>True when the path depth exceeds the hard-stop limit (5). When true the verb fails with a config error.</summary>
    public required bool DepthExceeded { get; init; }

    /// <summary>Non-empty when the operation partially or fully failed.</summary>
    public string? Error { get; init; }
}
