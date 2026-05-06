namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-plan-ado</c> — the Azure DevOps analogue
/// of <c>polyphony pr merge-plan-pr</c>. Same compound transactional verb
/// (lock → poll → identity check → stale-generation guard → complete PR
/// → record ledger → push manifest), wired against
/// <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient"/> instead of
/// <see cref="Polyphony.Infrastructure.Processes.IGhClient"/>.
///
/// <para>Workflow consumers route on
/// <see cref="ErrorCode"/> first, then on the combination of
/// <see cref="Merged"/> + <see cref="ManifestRecorded"/> +
/// <see cref="ManifestPushed"/>. Routing-style verb: always exits 0 so
/// workflow YAML branches on <see cref="ErrorCode"/>, not the process exit
/// code.</para>
///
/// <para><b>Diff validation deferred for v1.</b> The GitHub-side verb runs
/// a P8b plan-diff classification via
/// <c>gh.GetPullRequestFilesAsync</c>; the equivalent ADO endpoint is not
/// yet exposed on <c>IAdoClient</c>, so this verb skips that guard. The
/// stale-generation block (P6) is preserved — it operates on the manifest
/// at <c>origin/feature/{root}</c> and the PR body's front-matter, both of
/// which are platform-agnostic.</para>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrMergePlanAdoResult
{
    /// <summary>Run's root work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>Work-item id this plan PR belongs to.</summary>
    public required int ItemId { get; init; }

    /// <summary>Immediate plan-tree parent's work-item id; 0 when the parent is the root plan or this is the root plan.</summary>
    public required int ParentItemId { get; init; }

    /// <summary>Plan key form used in the manifest (<c>"root"</c> or numeric id as string).</summary>
    public required string ItemKey { get; init; }

    /// <summary>True when this PR is for the root plan.</summary>
    public required bool IsRootPlan { get; init; }

    /// <summary>Source branch the verb expected the PR to use.</summary>
    public required string HeadBranch { get; init; }

    /// <summary>Target branch the verb expected the PR to use.</summary>
    public required string BaseBranch { get; init; }

    /// <summary>Branch the manifest mutation is committed to (always the feature branch).</summary>
    public required string ManifestBranch { get; init; }

    /// <summary>ADO organization name (echo of <c>--organization</c>).</summary>
    public required string Organization { get; init; }

    /// <summary>ADO project name (echo of <c>--project</c>).</summary>
    public required string Project { get; init; }

    /// <summary>ADO repository identifier (echo of <c>--repository</c>; GUID or name).</summary>
    public required string Repository { get; init; }

    /// <summary>
    /// Composite slug — <c>{organization}/{project}/{repository}</c> — surfaced
    /// for cross-platform routing parity with
    /// <see cref="PrMergePlanPrResult.RepoSlug"/>. Empty when the verb errored
    /// before slug construction.
    /// </summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR number being acted on.</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL (canonical <c>dev.azure.com</c> page); empty when not yet built.</summary>
    public required string PrUrl { get; init; }

    /// <summary>PR state observed at poll time (<c>OPEN</c>, <c>MERGED</c>, <c>CLOSED</c>); empty when poll failed.</summary>
    public required string PrState { get; init; }

    /// <summary>True when the PR ended up merged (newly OR already).</summary>
    public required bool Merged { get; init; }

    /// <summary>True when the PR was already merged at poll time (no new merge call was issued by this invocation).</summary>
    public required bool AlreadyMerged { get; init; }

    /// <summary>Merge commit SHA when known; empty when not merged or platform did not return one.</summary>
    public required string MergeCommit { get; init; }

    /// <summary>True when this invocation appended a fresh entry to <c>merged_plan_prs</c>.</summary>
    public required bool ManifestRecorded { get; init; }

    /// <summary>True when this invocation committed and pushed the manifest mutation.</summary>
    public required bool ManifestPushed { get; init; }

    /// <summary>Generation before the bump; equals <see cref="CurrentGeneration"/> when the verb hit an idempotent skip.</summary>
    public required int PreviousGeneration { get; init; }

    /// <summary>Generation after the bump (or the prior recording's value on idempotent skip).</summary>
    public required int CurrentGeneration { get; init; }

    /// <summary>Run-lock token captured during the operation. Always populated for traceability; an empty string indicates the verb errored before acquiring the lock.</summary>
    public required string LockToken { get; init; }

    /// <summary>True when the verb successfully released the lock. False indicates a leaked lock that may need <c>polyphony lock force-release</c>.</summary>
    public required bool LockReleased { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>repo_not_resolved</c>, <c>lock_held</c>,
    /// <c>lock_stale</c>, <c>worktree_dirty</c>, <c>manifest_read_failed</c>,
    /// <c>pr_not_found</c>, <c>pr_identity_mismatch</c>,
    /// <c>pr_state_invalid</c>, <c>stale_generation</c>, <c>stale_head</c>,
    /// <c>ado_complete_failed</c>, <c>missing_merge_commit</c>,
    /// <c>ledger_conflict</c>, <c>manifest_push_rejected</c>,
    /// <c>ado_timeout</c>, <c>ado_failed</c>, <c>no_pat</c>,
    /// <c>internal_error</c>. Empty string on success.
    /// </summary>
    public required string ErrorCode { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Diff of stale ancestor entries when <see cref="ErrorCode"/> is
    /// <c>stale_generation</c>. Omitted otherwise. Reuses the GitHub-side
    /// <see cref="StaleAncestorEntry"/> shape — the diagnostic is
    /// platform-agnostic.
    /// </summary>
    public IReadOnlyList<StaleAncestorEntry>? StaleAncestors { get; init; }
}
