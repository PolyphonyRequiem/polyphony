namespace Polyphony;

/// <summary>
/// Result emitted by <c>polyphony plan recreate-stale-descendant</c> — the
/// second remedy policy outcome of the Phase 3 P9 cascade-remedy. When
/// auto-rebase is not policy-applicable (or has been overridden), this verb
/// closes the stale descendant plan PR, deletes its head branch (best-effort),
/// re-creates the plan branch from the current parent-plan tip, opens a
/// fresh PR with an up-to-date <c>ancestor_plan_generations</c> snapshot,
/// and records the recreation in the manifest's rebase ledger under reason
/// <c>child_plan_drift</c>.
///
/// <para>Workflow consumers route on <see cref="Outcome"/> first, then on
/// the partial-success booleans (<see cref="OldPrClosed"/>,
/// <see cref="OldBranchDeleted"/>, <see cref="NewBranchCreated"/>,
/// <see cref="NewPrOpened"/>, <see cref="ManifestRecorded"/>,
/// <see cref="ManifestPushed"/>). The verb is always exit-0 (routing-style);
/// non-zero exits indicate a genuinely unexpected exception.</para>
///
/// <para><b>Outcome taxonomy</b>:</para>
/// <list type="bullet">
///   <item><c>recreated</c> — Old PR closed, fresh PR opened, manifest recorded — all OK. Branch deletion may have failed (warning).</item>
///   <item><c>noop</c> — The "old" PR was already closed AND a fresh PR exists with current snapshot AND manifest has a ledger entry — replay safe.</item>
///   <item><c>parent_stale</c> — Cascade precondition violation (parent plan PR is itself stale).</item>
///   <item><c>pr_state_invalid</c> / <c>pr_identity_mismatch</c> / <c>pr_not_found</c> — Old PR doesn't match expectations.</item>
///   <item><c>lock_held</c> — Run lock held by someone else.</item>
///   <item><c>worktree_dirty</c> — Local worktree has uncommitted changes.</item>
///   <item><c>pr_close_failed</c> — gh pr close call failed.</item>
///   <item><c>branch_create_failed</c> — Could not re-create plan branch.</item>
///   <item><c>pr_open_failed</c> — Could not open the new PR.</item>
///   <item><c>manifest_push_rejected</c> — Branch + close + open OK, manifest push raced. Replay completes.</item>
///   <item><c>internal_error</c> — Unexpected exception.</item>
/// </list>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PlanRecreateStaleDescendantResult
{
    /// <summary>Run's root work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>Descendant work-item id whose stale PR is being recreated.</summary>
    public required int ItemId { get; init; }

    /// <summary>Immediate plan-tree parent's work-item id; equals <see cref="RootId"/> when the parent is the root plan.</summary>
    public required int ParentItemId { get; init; }

    /// <summary>Old PR number being closed and replaced.</summary>
    public required int OldPrNumber { get; init; }

    /// <summary>Old PR URL (canonical github.com page); empty when the verb errored before poll.</summary>
    public string OldPrUrl { get; init; } = string.Empty;

    /// <summary>Source branch the verb expected the old PR to use (<c>plan/{root}-{item}</c>). Populated as soon as inputs validate.</summary>
    public string OldHeadBranch { get; init; } = string.Empty;

    /// <summary>Parent plan branch derived from <see cref="ParentItemId"/> — <c>plan/{root}</c> for direct children of root, <c>plan/{root}-{parent}</c> otherwise.</summary>
    public string ParentPlanBranch { get; init; } = string.Empty;

    /// <summary>The categorical outcome — drives workflow routing. See class summary for taxonomy.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Newly-created PR number on the recreated head branch. Null when no new PR was opened (failure modes / noop where the existing fresh PR is reused).</summary>
    public int? NewPrNumber { get; init; }

    /// <summary>Newly-created PR URL on the recreated head branch. Null when no new PR was opened.</summary>
    public string? NewPrUrl { get; init; }

    /// <summary>The new head branch name (same shape as old head: <c>plan/{root}-{item}</c>); null when the verb errored before re-creation.</summary>
    public string? NewHeadBranch { get; init; }

    /// <summary>True when this invocation closed the old PR (gh pr close returned 0). Reused as a noop signal when the PR was already closed.</summary>
    public bool OldPrClosed { get; init; }

    /// <summary>True when the old PR's head branch was successfully removed from the remote. False when delete failed (warning, not terminal).</summary>
    public bool OldBranchDeleted { get; init; }

    /// <summary>True when this invocation re-created the plan branch from the parent tip and pushed it.</summary>
    public bool NewBranchCreated { get; init; }

    /// <summary>True when this invocation opened the new PR.</summary>
    public bool NewPrOpened { get; init; }

    /// <summary>True when this invocation appended a fresh entry to <c>rebases</c> in the manifest.</summary>
    public bool ManifestRecorded { get; init; }

    /// <summary>True when this invocation committed and pushed the manifest mutation to <c>feature/{root}</c>.</summary>
    public bool ManifestPushed { get; init; }

    /// <summary>Non-blocking warnings — e.g. "old branch delete failed; ignore if branch was already gone".</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Populated when the verb errored. Omitted on routing-success outcomes (<c>recreated</c>, <c>noop</c>).</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Categorical error code on a failure. One of: <c>invalid_argument</c>,
    /// <c>no_slug</c>, <c>manifest_read_failed</c>, <c>manifest_invalid</c>,
    /// <c>manifest_push_rejected</c>, <c>lock_held</c>, <c>worktree_dirty</c>,
    /// <c>pr_not_found</c>, <c>pr_identity_mismatch</c>, <c>pr_state_invalid</c>,
    /// <c>parent_stale</c>, <c>pr_close_failed</c>, <c>branch_create_failed</c>,
    /// <c>pr_open_failed</c>, <c>gh_failed</c>, <c>git_failed</c>,
    /// <c>internal_error</c>. Null on success outcomes (<c>recreated</c>, <c>noop</c>).
    /// </summary>
    public string? ErrorCode { get; init; }
}
