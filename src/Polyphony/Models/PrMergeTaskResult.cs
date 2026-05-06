namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-task-pr</c>: merges the per-item task PR
/// into its enclosing merge-group branch. Default merge method is squash;
/// override only when the planner has a reason. Branch identifiers are
/// echoed for workflow logging.
/// </summary>
public sealed record PrMergeTaskResult
{
    /// <summary>The PR number that was merged. Zero on error.</summary>
    public required int PrNumber { get; init; }

    /// <summary>The task branch (head). Format: <c>task/{root}-{item}</c>.</summary>
    public required string HeadBranch { get; init; }

    /// <summary>The enclosing merge-group branch (base).</summary>
    public required string BaseBranch { get; init; }

    /// <summary>The root work-item id.</summary>
    public required int RootId { get; init; }

    /// <summary>The item being merged.</summary>
    public required int ItemId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path the item belongs to.</summary>
    public required string MgPath { get; init; }

    /// <summary>The merge method that was used (<c>squash</c>, <c>merge</c>, or <c>rebase</c>).</summary>
    public required string Method { get; init; }

    /// <summary>True when the merge completed (newly issued or already-merged at start).</summary>
    public required bool Merged { get; init; }

    /// <summary>True when the PR was already merged before this verb ran.</summary>
    public required bool AlreadyMerged { get; init; }

    /// <summary>True when <c>--delete-branch</c> was passed to gh.</summary>
    public required bool DeleteBranch { get; init; }

    /// <summary>Merge commit SHA, when known. Null when merge failed or gh did not return one.</summary>
    public string? MergeSha { get; init; }

    /// <summary>Non-empty when the operation failed.</summary>
    public string? Error { get; init; }
}
