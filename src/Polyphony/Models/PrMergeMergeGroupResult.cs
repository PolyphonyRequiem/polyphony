namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-mg-pr</c>: merges a merge-group PR into
/// its parent branch. <see cref="Method"/> is always <c>merge</c> (per ADR
/// docs/decisions/branch-model.md — nested merge groups depend on git
/// ancestry to know what is integrated; squash and rebase would break the
/// chain). Branch identifiers are echoed for workflow logging.
/// </summary>
public sealed record PrMergeMergeGroupResult
{
    /// <summary>The PR number that was merged. Zero on error.</summary>
    public required int PrNumber { get; init; }

    /// <summary>The merge-group branch (head). Format: <c>mg/{root}_{mg_path}</c>.</summary>
    public required string HeadBranch { get; init; }

    /// <summary>
    /// The base branch — the parent merge-group branch when nested, or the
    /// feature branch when top-level.
    /// </summary>
    public required string BaseBranch { get; init; }

    /// <summary>The root work-item id.</summary>
    public required int RootId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path being merged.</summary>
    public required string MgPath { get; init; }

    /// <summary>Always the literal <c>"merge"</c> — included for workflow log clarity.</summary>
    public required string Method { get; init; }

    /// <summary>True when the merge completed (newly issued or already-merged at start).</summary>
    public required bool Merged { get; init; }

    /// <summary>True when the PR was already merged before this verb ran.</summary>
    public required bool AlreadyMerged { get; init; }

    /// <summary>
    /// Always false for merge-group PRs — nested MG branches must persist
    /// for the ancestry chain. Included in the output for symmetry with
    /// <c>PrMergeImplResult</c>.
    /// </summary>
    public required bool DeleteBranch { get; init; }

    /// <summary>Merge commit SHA, when known. Null when merge failed or gh did not return one.</summary>
    public string? MergeSha { get; init; }

    /// <summary>Non-empty when the operation failed.</summary>
    public string? Error { get; init; }
}
