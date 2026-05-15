namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-impl-pr</c>: merges the per-item impl PR
/// into its enclosing merge-group branch. Default merge method is squash;
/// override only when the planner has a reason. Branch identifiers are
/// echoed for workflow logging.
///
/// <para>The verb is platform-aware: when <c>--platform ado</c> is
/// supplied (or the resolver detects an ADO origin), the ADO-specific
/// fields below are populated and the verb internally dispatches to the
/// same logic as <c>polyphony pr merge-impl-ado</c>. On the GitHub leg
/// these fields stay empty.</para>
/// </summary>
public sealed record PrMergeImplResult
{
    /// <summary>The PR number that was merged. Zero on error.</summary>
    public required int PrNumber { get; init; }

    /// <summary>The impl branch (head). Format: <c>impl/{root}-{item}</c>.</summary>
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

    /// <summary>ADO organization (populated only on the ADO leg).</summary>
    public string Organization { get; init; } = string.Empty;

    /// <summary>ADO project (populated only on the ADO leg).</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>ADO repository name or GUID (populated only on the ADO leg).</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>Canonical platform-prefixed slug.</summary>
    public string RepoSlug { get; init; } = string.Empty;

    /// <summary>Non-empty when the operation failed.</summary>
    public string? Error { get; init; }
}
