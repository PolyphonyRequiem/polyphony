namespace Polyphony;

/// <summary>
/// Output of <c>polyphony branch ensure-feature</c>: confirms the feature
/// branch exists locally and on the remote, creating it idempotently if
/// necessary. The apex workflow calls this once; sub-workflows receive the
/// branch name as an input and trust it.
/// </summary>
public sealed record BranchEnsureFeatureResult
{
    /// <summary>The feature branch name that was ensured.</summary>
    public required string Branch { get; init; }

    /// <summary><c>created</c> | <c>existed</c> | <c>checked_out</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Whether the branch already existed on the remote before this call.</summary>
    public required bool RemoteExisted { get; init; }

    /// <summary>Whether the branch was pushed to the remote by this call.</summary>
    public required bool Pushed { get; init; }

    /// <summary>The base branch/ref the feature branch was created from (only set when action=created).</summary>
    public string? CreatedFrom { get; init; }

    /// <summary>Non-empty when the operation partially or fully failed.</summary>
    public string? Error { get; init; }
}
