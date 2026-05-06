namespace Polyphony;

/// <summary>
/// Output of <c>polyphony branch ensure-impl</c>: confirms the impl branch
/// exists locally and on the remote, materializing the parent merge-group
/// branch first if it exists only remotely. The verb is idempotent — if
/// the branch already exists nothing is created and no push is performed.
/// </summary>
public sealed record BranchEnsureImplResult
{
    /// <summary>The fully-qualified impl branch name (e.g. <c>impl/123-456</c>).</summary>
    public required string Branch { get; init; }

    /// <summary>The base merge-group branch the impl branch was (or would be) created from.</summary>
    public required string BaseBranch { get; init; }

    /// <summary><c>created</c> | <c>checked_out</c> | <c>error</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Whether the impl branch already existed on the remote before this call.</summary>
    public required bool RemoteExisted { get; init; }

    /// <summary>Whether the impl branch was pushed to the remote by this call.</summary>
    public required bool Pushed { get; init; }

    /// <summary>Whether the base merge-group branch existed on the remote before this call.</summary>
    public required bool BaseRemoteExisted { get; init; }

    /// <summary>Whether the base branch was fetched from the remote by this call.</summary>
    public required bool BaseFetched { get; init; }

    /// <summary>The base branch the impl branch was created from (only set when action=created).</summary>
    public string? CreatedFrom { get; init; }

    /// <summary>The root work-item id supplied as input.</summary>
    public required int RootId { get; init; }

    /// <summary>The non-root work-item id supplied as input.</summary>
    public required int ItemId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path supplied as input.</summary>
    public required string MgPath { get; init; }

    /// <summary>Non-empty when the operation partially or fully failed.</summary>
    public string? Error { get; init; }
}
