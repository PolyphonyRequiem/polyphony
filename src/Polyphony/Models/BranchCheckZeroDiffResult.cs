namespace Polyphony;

/// <summary>
/// Output of <c>polyphony branch check-zero-diff</c>: determines whether the
/// feature branch has zero commits ahead of the configured target (typically
/// <c>main</c>). Used at apex dispatch time to short-circuit when a prior
/// run's changes have already been merged to the target — prevents the
/// dispatch loop from creating empty merge groups (AB#3127 / AB#3175).
/// </summary>
public sealed record BranchCheckZeroDiffResult
{
    /// <summary>
    /// <c>true</c> when the feature branch is an ancestor of (or identical to)
    /// the target branch on the remote — i.e. it has zero unique commits.
    /// </summary>
    public required bool ZeroDiff { get; init; }

    /// <summary>The feature branch that was checked (e.g. <c>feature/3165</c>).</summary>
    public required string FeatureBranch { get; init; }

    /// <summary>
    /// The target branch compared against, sourced from
    /// <c>branch_strategy.target</c> in <c>process-config.yaml</c> (P5).
    /// </summary>
    public required string TargetBranch { get; init; }

    /// <summary>Non-empty when the check could not be performed.</summary>
    public string? Error { get; init; }
}
