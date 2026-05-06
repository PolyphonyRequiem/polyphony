namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-feature-ado</c> — the Azure DevOps analogue
/// of the GitHub <c>gh pr merge --squash</c> path used by <c>github-pr.yaml</c>
/// for feature PRs. Merges a feature PR into its target branch (typically
/// <c>main</c>) via <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient.CompletePullRequestAsync"/>
/// which currently pins the strategy to <c>noFastForward</c>.
///
/// <para><b>Note on merge strategy:</b> GitHub's feature-PR flow squashes;
/// ADO's <see cref="Polyphony.Infrastructure.AzureDevOps.AdoCompletionOptions"/>
/// hardcodes <c>noFastForward</c>. The semantic difference is preserved
/// intentionally — selecting a per-call merge strategy requires extending
/// <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient.CompletePullRequestAsync"/>
/// and is deferred. <c>noFastForward</c> on a feature PR still produces a
/// merge commit on <c>main</c> that captures the full feature history, which
/// is acceptable for the tracking surface; squash-equivalence can be added
/// later without changing the verb's I/O contract.</para>
///
/// <para><b>Routing-style exit code</b> — always exits 0; consumers branch
/// on <see cref="ErrorCode"/>. Mirrors <c>merge-mg-ado</c> (#106) and
/// <c>merge-plan-ado</c> (#104).</para>
///
/// <para><b>No <c>--admin</c> flag</b>: ADO bypasses branch-protection
/// policies via the <c>completionOptions.bypassPolicy</c> field on the
/// complete-PR call, which is pinned to <c>false</c>. Exposing a CLI bypass
/// flag is deferred — same deferral as #104 (<c>merge-plan-ado</c>) and
/// #106 (<c>merge-mg-ado</c>).</para>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrMergeFeatureAdoResult
{
    /// <summary>The root work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>The feature branch (head). Format: <c>feature/{root}</c>.</summary>
    public required string HeadBranch { get; init; }

    /// <summary>The target branch (base) — typically <c>main</c>.</summary>
    public required string BaseBranch { get; init; }

    /// <summary>ADO organization name (echo of <c>--organization</c>).</summary>
    public required string Organization { get; init; }

    /// <summary>ADO project name (echo of <c>--project</c>).</summary>
    public required string Project { get; init; }

    /// <summary>ADO repository identifier (echo of <c>--repository</c>; GUID or name).</summary>
    public required string Repository { get; init; }

    /// <summary>
    /// Composite slug — <c>{organization}/{project}/{repository}</c> — surfaced
    /// for cross-platform routing parity with GitHub-side feature-PR consumers.
    /// Empty when the verb errored before slug construction.
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

    /// <summary>
    /// Always the literal <c>"merge"</c> — included for workflow log clarity.
    /// Reflects the underlying ADO <c>noFastForward</c> strategy (which
    /// produces a merge commit, not a squash).
    /// </summary>
    public required string Method { get; init; }

    /// <summary>True when the merge completed (newly issued or already-merged at start).</summary>
    public required bool Merged { get; init; }

    /// <summary>True when the PR was already merged before this verb ran.</summary>
    public required bool AlreadyMerged { get; init; }

    /// <summary>
    /// Always false — feature branches must persist post-merge so subsequent
    /// run resumes can locate their integration trunk. The GitHub-side feature
    /// flow uses <c>--delete-branch</c>; we deliberately diverge here because
    /// polyphony's run manifest lives at <c>feature/{root}:.polyphony/run.yaml</c>.
    /// Included in the output for symmetry with merge-mg/merge-plan results.
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
