namespace Polyphony;

/// <summary>
/// Result emitted by <c>polyphony plan detect-state</c>. Reports the
/// observable state of a plan workflow for a given work item by inspecting
/// the plan branch on origin, the most-recent plan PR, the run manifest's
/// plan-generation ledger, and (for merged PRs) the parent's
/// <c>polyphony:planned</c> tag.
///
/// <para>The state values form a small, plan-level state machine consumed
/// by <c>plan-level.yaml</c> to decide what to do next:</para>
/// <list type="bullet">
///   <item><c>not_started</c> — no plan PR exists. Run the architect.</item>
///   <item><c>awaiting_review</c> — plan PR is open. Poll for review status.</item>
///   <item><c>stale_generation</c> — open PR's <c>ancestor_plan_generations</c>
///         snapshot is behind the manifest. Re-run the architect against the
///         current ancestor plans.</item>
///   <item><c>closed_unmerged</c> — plan PR was closed without merging. Surface
///         to a human gate; the workflow cannot proceed.</item>
///   <item><c>merged_unseeded</c> — plan PR is merged but the parent work item
///         doesn't carry the planned tag yet. Run <c>polyphony plan seed-children</c>.</item>
///   <item><c>complete</c> — plan PR is merged AND the parent carries the
///         planned tag. Workflow is done.</item>
///   <item><c>parent_change_pending</c> — plan PR is merged AND the parent
///         carries the planned tag, BUT one or more direct children have an
///         approved plan PR with <c>requests_parent_change: true</c>. The
///         parent's plan must be re-opened to incorporate the requested
///         changes. <see cref="ParentChangePendingChildren"/> lists the
///         qualifying child PRs.</item>
/// </list>
/// </summary>
public sealed record PlanDetectStateResult
{
    public required int RootId { get; init; }

    public required int ItemId { get; init; }

    /// <summary>Plan branch name we inspected (e.g. <c>plan/100-101</c>).</summary>
    public required string PlanBranch { get; init; }

    /// <summary>The detected state — one of the values described in the type doc.</summary>
    public required string State { get; init; }

    /// <summary>True when <c>git ls-remote --heads origin {plan_branch}</c> returned a hit.</summary>
    public required bool BranchExistsOnOrigin { get; init; }

    /// <summary>PR number when a PR (open or closed) was found for this branch; null otherwise.</summary>
    public int? PrNumber { get; init; }

    /// <summary>PR URL when a PR was found.</summary>
    public string? PrUrl { get; init; }

    /// <summary>Raw gh PR state — <c>OPEN</c> | <c>CLOSED</c> | <c>MERGED</c> — when a PR was found.</summary>
    public string? PrState { get; init; }

    /// <summary>
    /// When <see cref="State"/> is <c>stale_generation</c>, lists the ancestor keys
    /// whose manifest plan-generation has advanced past the PR's snapshot, e.g.
    /// <c>"root: snapshot=1, manifest=2"</c>. Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> StaleAncestors { get; init; } = [];

    /// <summary>
    /// When <see cref="State"/> is <c>parent_change_pending</c>, lists the
    /// direct-child plan PRs that are approved and request a change to the
    /// parent's plan. Empty otherwise. Each entry carries enough metadata
    /// for <c>polyphony plan extract-parent-patch</c> to render the child's
    /// proposed parent-plan diff.
    /// </summary>
    public IReadOnlyList<ChildPendingParentChange> ParentChangePendingChildren { get; init; } = [];

    /// <summary>Error message on failure; null on success.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Single entry in <see cref="PlanDetectStateResult.ParentChangePendingChildren"/>.
/// Identifies one direct-child plan PR that triggered the parent
/// <c>parent_change_pending</c> state.
/// </summary>
/// <param name="ChildItemId">Work item ID of the child whose plan PR requested parent change.</param>
/// <param name="PrNumber">GitHub PR number for the child's plan PR.</param>
/// <param name="PrUrl">Canonical URL for the child's plan PR.</param>
/// <param name="ExpectedParentGeneration">Parent generation captured in the
/// child PR's <c>ancestor_plan_generations</c> snapshot. Equals the parent's
/// current generation at the moment of detection (otherwise the child PR
/// would be classified stale rather than pending parent change).</param>
public sealed record ChildPendingParentChange(
    int ChildItemId,
    int PrNumber,
    string PrUrl,
    int ExpectedParentGeneration);
