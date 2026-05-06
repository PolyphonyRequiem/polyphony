namespace Polyphony;

/// <summary>
/// Result emitted by <c>polyphony plan rebase-stale-descendant</c> — the
/// compound-transactional remedy verb that auto-rebases a stale descendant
/// plan PR onto the current parent-plan tip when the manifest's
/// <c>plan_generations</c> have advanced past the snapshot embedded in the
/// PR body's front-matter.
///
/// <para>Workflow consumers route on <see cref="Outcome"/> first, then on
/// the partial-success booleans (<see cref="BodyUpdated"/>,
/// <see cref="ManifestRecorded"/>, <see cref="ManifestPushed"/>,
/// <see cref="CommentPosted"/>). The verb is always exit-0 (routing-style);
/// non-zero exits indicate a genuinely unexpected exception.</para>
///
/// <para><b>Outcome taxonomy</b> (see Phase 3 P9 cascade-remedy design doc):</para>
/// <list type="bullet">
///   <item><c>rebased</c> — Clean rebase, push, body, manifest all OK. Comment may have failed (warning).</item>
///   <item><c>noop</c> — All three freshness facts already true (branch ancestor, body fresh, ledger present).</item>
///   <item><c>conflict</c> — Rebase conflicts; aborted; workflow should route to human_gate.</item>
///   <item><c>parent_stale</c> — Cascade precondition violation (parent plan PR is itself stale).</item>
///   <item><c>pr_head_changed</c> — Lease failure or fetch-vs-poll race.</item>
///   <item><c>pr_state_invalid</c> / <c>pr_identity_mismatch</c> / <c>pr_not_found</c> — PR doesn't match expectations.</item>
///   <item><c>lock_held</c> — Run lock held by someone else.</item>
///   <item><c>worktree_dirty</c> — Local worktree has uncommitted changes.</item>
///   <item><c>malformed_front_matter</c> — PR body's front-matter doesn't ParseStrict.</item>
///   <item><c>body_update_failed</c> — Branch pushed but body edit failed (partial; replay completes).</item>
///   <item><c>manifest_push_rejected</c> — Branch + body OK, manifest push raced (replay completes).</item>
///   <item><c>internal_error</c> — Unexpected exception.</item>
/// </list>
///
/// <para>Snake-case via the global JsonSerializerOptions on
/// <see cref="PolyphonyJsonContext"/>.</para>
/// </summary>
public sealed record PlanRebaseStaleDescendantResult
{
    /// <summary>Run's root work-item id, echoed for traceability.</summary>
    public required int RootId { get; init; }

    /// <summary>Descendant work-item id whose PR is being rebased.</summary>
    public required int ItemId { get; init; }

    /// <summary>Immediate plan-tree parent's work-item id; equals <see cref="RootId"/> when the parent is the root plan.</summary>
    public required int ParentItemId { get; init; }

    /// <summary>PR number being rebased.</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL (canonical github.com page); empty when the verb errored before poll.</summary>
    public string PrUrl { get; init; } = string.Empty;

    /// <summary>Source branch the verb expected the PR to use (<c>plan/{root}-{item}</c>). Populated as soon as inputs validate.</summary>
    public string HeadBranch { get; init; } = string.Empty;

    /// <summary>Parent plan branch derived from <see cref="ParentItemId"/> — <c>plan/{root}</c> for direct children of root, <c>plan/{root}-{parent}</c> otherwise.</summary>
    public string ParentPlanBranch { get; init; } = string.Empty;

    /// <summary>The categorical outcome — drives workflow routing. See class summary for taxonomy.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>PR head SHA observed at poll time; null when the verb errored before poll.</summary>
    public string? OldHeadSha { get; init; }

    /// <summary>HEAD SHA after the rebase; null when no rebase ran (noop / conflict / pre-rebase failure).</summary>
    public string? NewHeadSha { get; init; }

    /// <summary>Conflicted file paths reported by git — populated only when <see cref="Outcome"/> is <c>conflict</c>.</summary>
    public IReadOnlyList<string> ConflictFiles { get; init; } = [];

    /// <summary>True when the best-effort comment-post succeeded. Always false when no comment was attempted.</summary>
    public bool CommentPosted { get; init; }

    /// <summary>True when this invocation rewrote the PR body's front-matter snapshot.</summary>
    public bool BodyUpdated { get; init; }

    /// <summary>True when this invocation appended a fresh entry to <c>rebases</c> in the manifest.</summary>
    public bool ManifestRecorded { get; init; }

    /// <summary>True when this invocation committed and pushed the manifest mutation to <c>feature/{root}</c>.</summary>
    public bool ManifestPushed { get; init; }

    /// <summary>Non-blocking warnings — e.g. "comment-post failed" or "fetch returned no new commits".</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Populated when the verb errored. Omitted on routing-success outcomes (<c>rebased</c>, <c>noop</c>).</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Categorical error code on a failure. One of: <c>invalid_argument</c>,
    /// <c>no_slug</c>, <c>manifest_read_failed</c>, <c>manifest_invalid</c>,
    /// <c>manifest_push_rejected</c>, <c>lock_held</c>, <c>worktree_dirty</c>,
    /// <c>pr_not_found</c>, <c>pr_identity_mismatch</c>, <c>pr_state_invalid</c>,
    /// <c>pr_head_changed</c>, <c>parent_stale</c>, <c>malformed_front_matter</c>,
    /// <c>rebase_conflict</c>, <c>rebase_failed</c>, <c>body_update_failed</c>,
    /// <c>gh_failed</c>, <c>git_failed</c>, <c>internal_error</c>. Null on
    /// success outcomes (<c>rebased</c>, <c>noop</c>).
    /// </summary>
    public string? ErrorCode { get; init; }
}
