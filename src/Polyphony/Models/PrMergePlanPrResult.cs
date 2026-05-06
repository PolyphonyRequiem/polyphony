namespace Polyphony;

/// <summary>
/// Output of <c>polyphony pr merge-plan-pr</c> — the compound transactional
/// verb that merges a plan PR (head = <c>plan/{root}-{item_id}</c> or
/// <c>plan/{root}</c>; base = parent plan branch or feature branch),
/// records the merge in the run manifest's <c>merged_plan_prs</c> ledger,
/// and pushes the manifest mutation back to the feature branch — all
/// under the same-root run lock.
///
/// <para>Workflow consumers route on the combination of
/// <see cref="Merged"/> + <see cref="ManifestRecorded"/> +
/// <see cref="ManifestPushed"/> + <see cref="ErrorCode"/>:</para>
/// <list type="bullet">
///   <item><c>Merged=true, ManifestRecorded=true, ManifestPushed=true, Error=null</c> — full success on a fresh merge.</item>
///   <item><c>Merged=true, AlreadyMerged=true, ManifestRecorded=false, ManifestPushed=false, Error=null</c> — fully idempotent re-entry: PR was already merged AND its merge was already recorded in the ledger. Workflow may continue.</item>
///   <item><c>Merged=true, AlreadyMerged=true, ManifestRecorded=true, ManifestPushed=true, Error=null</c> — partial-failure recovery: PR merged on a prior attempt but manifest wasn't recorded; this attempt recorded and pushed it.</item>
///   <item><c>Merged=false, ErrorCode</c> populated — verb refused or could not complete; details in <see cref="Error"/>.</item>
/// </list>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PrMergePlanPrResult
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

    /// <summary>Owner/repo slug; empty when the verb couldn't resolve it.</summary>
    public required string RepoSlug { get; init; }

    /// <summary>PR number being acted on.</summary>
    public required int PrNumber { get; init; }

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
    /// <c>config_error</c>, <c>worktree_dirty</c>, <c>repo_not_resolved</c>,
    /// <c>lock_held</c>, <c>lock_stale</c>, <c>pr_not_found</c>,
    /// <c>head_ref_mismatch</c>, <c>base_ref_mismatch</c>,
    /// <c>pr_state_unmergeable</c>, <c>merge_failed</c>,
    /// <c>missing_merge_commit</c>, <c>ledger_conflict</c>,
    /// <c>manifest_push_rejected</c>, <c>stale_generation</c>,
    /// <c>validation_blocked</c>, <c>internal_error</c>. Empty string on success.
    /// </summary>
    public required string ErrorCode { get; init; }

    /// <summary>Populated when the verb errored. Omitted on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Diff of stale ancestor entries when <see cref="ErrorCode"/> is
    /// <c>stale_generation</c>. Omitted otherwise. Each entry names an
    /// ancestor key whose generation in the manifest has advanced past
    /// the snapshot embedded in the PR body's front-matter — i.e. another
    /// plan-PR for that ancestor merged after this PR was opened.
    /// </summary>
    public IReadOnlyList<StaleAncestorEntry>? StaleAncestors { get; init; }
}

/// <summary>One stale-ancestor diff entry surfaced by <see cref="PrMergePlanPrResult.StaleAncestors"/>.</summary>
public sealed record StaleAncestorEntry
{
    /// <summary>Ancestor key as it appears in the manifest (<c>"root"</c> or numeric id as string).</summary>
    public required string AncestorKey { get; init; }

    /// <summary>Generation captured in the PR body's snapshot at PR-open time.</summary>
    public required int SnapshotGeneration { get; init; }

    /// <summary>Generation currently recorded in the manifest.</summary>
    public required int CurrentGeneration { get; init; }
}
