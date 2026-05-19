namespace Polyphony;

/// <summary>
/// Output envelope for <c>polyphony reset facets --apex N</c> — strips
/// the two persisted "planning is already done" tags from the apex root
/// and every descendant in scope:
///
/// <list type="bullet">
///   <item><c>polyphony:facets=&lt;csv&gt;</c> — the per-item facet
///         override stamped by <see cref="Commands.PlanCommands.SeedChildren"/>
///         when the architect declared <c>apex_facets</c> in plan
///         front-matter. While present, <c>Sdlc.RequirementInputResolver</c>
///         overrides the type-config default facet set and the work item
///         skips the <c>plannable</c> facet on classify-lifecycle.</item>
///   <item><c>polyphony:planned</c> — the bare "this item has been
///         planned" marker stamped on plan-merge by
///         <see cref="Commands.PlanCommands.SeedChildren"/>; consulted by
///         the plan-level workflow's resume-detection gate
///         (<c>Sdlc.Observers.PlanObserver</c>).</item>
/// </list>
///
/// <para>The watermark mechanism (<c>polyphony:run-started-at</c>) can
/// only filter PR-based observations by merge time; it cannot demote a
/// persisted planning decision. So when an operator throws away a plan
/// branch via <see cref="Commands.ResetCommands.ResetBranches"/>, the
/// matching facet/planned tags would survive on the work item and steer
/// the next classify-lifecycle call away from <c>plan-level</c> —
/// reproducing the apex 62286666 incident
/// (<c>docs/decisions/run-reset.md</c> §"Why facets cleanup is separate
/// from watermark").</para>
///
/// <para>Routing-style envelope: always exits 0. Per-item failures
/// surface in <see cref="Items"/> as entries with <see cref="ResetFacetsItem.Verified"/>
/// = false and a non-null <see cref="ResetFacetsItem.Error"/>; the verb
/// continues across siblings. <see cref="Success"/> reflects "the walk
/// itself completed" — a verb-wide failure (twig sync crashed,
/// hierarchy walk threw) flips <see cref="Success"/> to false with
/// <see cref="Error"/> populated. Use this to mirror the
/// <see cref="ResetPrsResult"/> /
/// <see cref="ResetBranchesResult"/> per-item-failure tolerance.</para>
/// </summary>
public sealed record ResetFacetsResult
{
    /// <summary>Apex root work-item ID (mirrors <c>--apex</c>).</summary>
    public required int Apex { get; init; }

    /// <summary>True when the walk and per-item processing completed without a verb-wide error.</summary>
    public required bool Success { get; init; }

    /// <summary>True when this was a dry-run preview (no writes performed).</summary>
    public required bool DryRun { get; init; }

    /// <summary>Number of work items visited in the apex subtree (root + descendants).</summary>
    public int ItemsScanned { get; init; }

    /// <summary>
    /// Number of items that had at least one targeted tag to remove. In
    /// dry-run mode this is the count of items that WOULD be modified;
    /// in execute mode it is the count of items where the patch +
    /// read-after-write verification succeeded.
    /// </summary>
    public int ItemsModified { get; init; }

    /// <summary>Total count of <c>polyphony:facets=*</c> tags removed across all items.</summary>
    public int TotalFacetTagsRemoved { get; init; }

    /// <summary>Total count of <c>polyphony:planned</c> tags removed across all items.</summary>
    public int TotalPlannedTagsRemoved { get; init; }

    /// <summary>
    /// Per-item entries — one per work item that had at least one tag
    /// to remove. Items with no targeted tags are silently skipped and
    /// do NOT appear here (keeps the envelope small for deep trees).
    /// </summary>
    public IReadOnlyList<ResetFacetsItem> Items { get; init; } = [];

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Per-item result inside a <see cref="ResetFacetsResult"/>. One entry
/// per work item where at least one <c>polyphony:facets=*</c> or
/// <c>polyphony:planned</c> tag was present.
/// </summary>
public sealed record ResetFacetsItem
{
    /// <summary>The work item ID this entry describes.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>The verbatim <c>polyphony:facets=*</c> tag values removed (preserves casing).</summary>
    public required IReadOnlyList<string> FacetTagsRemoved { get; init; }

    /// <summary>True when the <c>polyphony:planned</c> bare tag was present and removed.</summary>
    public required bool PlannedTagRemoved { get; init; }

    /// <summary>
    /// Null in dry-run (no write performed, no verification). In execute
    /// mode: true when the read-after-write check confirmed the targeted
    /// tags are absent; false when the patch + sync exited 0 but the
    /// tags still appear in the post-sync cache (ADO eventual-consistency
    /// race — same defense as <see cref="Commands.BranchCommands.MarkImplMerged"/>).
    /// </summary>
    public bool? Verified { get; init; }

    /// <summary>Error message when this item failed to be processed. Null on success.</summary>
    public string? Error { get; init; }
}
