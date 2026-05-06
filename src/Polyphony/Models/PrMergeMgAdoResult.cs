namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-mg-ado</c> — the Azure DevOps analogue of
/// <c>polyphony pr merge-mg-pr</c>. Merges a merge-group PR into its parent
/// branch via <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient.CompletePullRequestAsync"/>
/// which pins the strategy to <c>noFastForward</c> per ADR
/// <c>docs/decisions/branch-model.md</c> — nested merge groups depend on git
/// ancestry to know what is integrated; squash and rebase would break the
/// chain. The head branch is never deleted (sibling merge groups may still
/// be in flight).
///
/// <para><b>Routing-style exit code</b> — always exits 0; consumers branch
/// on <see cref="ErrorCode"/>.</para>
///
/// <para><b>No <c>--admin</c> flag</b>: ADO bypasses branch-protection
/// policies via the <c>completionOptions.bypassPolicy</c> field on the
/// complete-PR call, which is pinned to <c>false</c> in the current
/// <see cref="Polyphony.Infrastructure.AzureDevOps.AdoCompletionOptions"/>
/// shape. Exposing a CLI bypass flag is deferred — same deferral as #104
/// (<c>merge-plan-ado</c>).</para>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrMergeMgAdoResult
{
    /// <summary>The root work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>The canonical <c>_</c>-joined merge-group path being merged.</summary>
    public required string MgPath { get; init; }

    /// <summary>The merge-group branch (head). Format: <c>mg/{root}_{mg_path}</c>.</summary>
    public required string HeadBranch { get; init; }

    /// <summary>
    /// The base branch — the parent merge-group branch when nested, or the
    /// feature branch when top-level.
    /// </summary>
    public required string BaseBranch { get; init; }

    /// <summary>ADO organization name (echo of <c>--organization</c>).</summary>
    public required string Organization { get; init; }

    /// <summary>ADO project name (echo of <c>--project</c>).</summary>
    public required string Project { get; init; }

    /// <summary>ADO repository identifier (echo of <c>--repository</c>; GUID or name).</summary>
    public required string Repository { get; init; }

    /// <summary>
    /// Composite slug — <c>{organization}/{project}/{repository}</c> — surfaced
    /// for cross-platform routing parity with
    /// <see cref="PrMergeMergeGroupResult"/> consumers. Empty when the verb
    /// errored before slug construction.
    /// </summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR number being acted on. Zero when no PR was found.</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL (canonical <c>dev.azure.com</c> page); empty when no PR was found.</summary>
    public required string PrUrl { get; init; }

    /// <summary>
    /// PR state observed at poll time (<c>OPEN</c>, <c>MERGED</c>,
    /// <c>CLOSED</c>); empty when the lookup failed before reading state.
    /// </summary>
    public required string PrState { get; init; }

    /// <summary>Always the literal <c>"merge"</c> — included for workflow log clarity (mirrors <see cref="PrMergeMergeGroupResult.Method"/>).</summary>
    public required string Method { get; init; }

    /// <summary>True when the merge completed (newly issued or already-merged at start).</summary>
    public required bool Merged { get; init; }

    /// <summary>True when the PR was already merged before this verb ran.</summary>
    public required bool AlreadyMerged { get; init; }

    /// <summary>
    /// Always false for merge-group PRs — nested MG branches must persist for
    /// the ancestry chain. Included in the output for symmetry with
    /// <see cref="PrMergeMergeGroupResult.DeleteBranch"/>.
    /// </summary>
    public required bool DeleteBranch { get; init; }

    /// <summary>Merge commit SHA when known; empty when not merged or platform did not return one.</summary>
    public required string MergeCommit { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>pr_not_found</c>, <c>pr_state_invalid</c>,
    /// <c>stale_head</c>, <c>missing_merge_commit</c>,
    /// <c>ado_complete_failed</c>, <c>no_pat</c>, <c>ado_timeout</c>,
    /// <c>ado_failed</c>. Empty string on success.
    /// </summary>
    public required string ErrorCode { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }
}
