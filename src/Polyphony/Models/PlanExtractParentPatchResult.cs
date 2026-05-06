namespace Polyphony;

/// <summary>
/// Result emitted by <c>polyphony plan extract-parent-patch</c>. Renders a
/// bounded, deterministic markdown patch for a single parent's plan file
/// out of an approved child plan PR that carries
/// <c>requests_parent_change: true</c>.
///
/// <para>The artifact is consumed by the parent's replan loop in
/// <c>plan-level.yaml</c> (P8d → workflow PR in the sibling repo): instead
/// of asking the parent architect to infer the request from an arbitrary
/// PR, we hand it a deterministic markdown diff scoped to exactly the
/// hunks that touch <c>plans/plan-{parent_item_id}.md</c>.</para>
///
/// <para>Always exits 0 (routing-style verb). Workflow branches on the
/// presence of <see cref="Error"/>.</para>
/// </summary>
public sealed record PlanExtractParentPatchResult
{
    /// <summary>The PR URL that was inspected.</summary>
    public required string PrUrl { get; init; }

    /// <summary>Parsed PR number on success; 0 on URL-parse failure.</summary>
    public int PrNumber { get; init; }

    /// <summary>Parsed repo slug (<c>owner/repo</c>) on success; empty on failure.</summary>
    public string RepoSlug { get; init; } = string.Empty;

    /// <summary>Work item ID inferred from the PR's plan branch (<c>plan/{root}-{child}</c>).</summary>
    public int? ChildItemId { get; init; }

    /// <summary>The parent work item ID this patch is targeted at (input).</summary>
    public required int ParentItemId { get; init; }

    /// <summary>
    /// The parent's plan generation as captured in the PR's
    /// <c>ancestor_plan_generations</c> snapshot (when present). Lets the
    /// parent replan loop verify it isn't acting on a stale request.
    /// </summary>
    public int? ExpectedParentGeneration { get; init; }

    /// <summary>
    /// The PR's head commit SHA at the moment of extraction. Lets the
    /// parent replan loop tie its decision back to a specific snapshot
    /// of the child PR.
    /// </summary>
    public string? HeadSha { get; init; }

    /// <summary>
    /// Files in the PR diff that match the parent plan file pattern
    /// (<c>plans/plan-{parent_item_id}.md</c>). Single element on the
    /// happy path; empty when the PR doesn't actually touch the parent's
    /// plan (warns; not an error).
    /// </summary>
    public IReadOnlyList<string> FilesTouched { get; init; } = [];

    /// <summary>
    /// The bounded markdown diff for the parent plan file. Includes the
    /// <c>diff --git</c> header, hunk headers (<c>@@</c>), and per-line
    /// markers (<c>+</c>/<c>-</c>/space) so the parent architect can
    /// reason about it as ordinary patch text.
    /// </summary>
    public string ParentPlanDiff { get; init; } = string.Empty;

    /// <summary>
    /// True when <see cref="ParentPlanDiff"/> was truncated to fit
    /// <c>--diff-size-limit-bytes</c>. A truncation notice is appended
    /// to the diff body itself.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Total bytes in the unbounded parent-scoped diff before truncation
    /// — useful for telemetry and routing decisions ("if the diff is
    /// huge, route to a human gate").
    /// </summary>
    public int DiffSizeBytes { get; init; }

    /// <summary>
    /// Value of the PR body's <c>requests_parent_change</c> flag. False
    /// is a warning (the PR shouldn't be requesting parent change at
    /// all); the verb still emits the diff for inspection.
    /// </summary>
    public bool RequestsParentChange { get; init; }

    /// <summary>
    /// Non-blocking warnings — e.g., flag not set, no matching files,
    /// snapshot missing the parent's generation entry.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Error message on a verb-level failure; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Error code on a verb-level failure; null on success.</summary>
    public string? ErrorCode { get; init; }
}
