namespace Polyphony.Sdlc;

/// <summary>
/// Canonical requirement kind string constants. Each kind is a condition the
/// owning work item must satisfy before close-out, derived from the item's
/// facet set, decomposability, facet order, and (for actionable) executor.
/// </summary>
/// <remarks>
/// <para>
/// Plannable family (three levels reflect the plan-gate granularity from the
/// glossary — a child plan can begin once its parent's plan is reviewed,
/// but child code waits for the parent plan to be promoted into the feature
/// branch):
/// </para>
/// <list type="bullet">
///   <item><description><c>plan_authored</c>: a plan document exists on the plan branch.</description></item>
///   <item><description><c>plan_reviewed</c>: the plan PR has aggregated status <c>approved</c>.</description></item>
///   <item><description><c>plan_promoted</c>: the plan PR is merged into its parent branch.</description></item>
/// </list>
/// <para>Decomposition family (plannable + decomposable only):</para>
/// <list type="bullet">
///   <item><description><c>children_seeded</c>: child work items have been created
///   per the promoted plan.</description></item>
/// </list>
/// <para>Implementation family (implementable):</para>
/// <list type="bullet">
///   <item><description><c>implementation_merged</c>: code changes are merged into the
///   parent merge group branch (or feature branch). Coarse on purpose for now —
///   PR-internal review is not surfaced as a separate requirement at this layer.</description></item>
/// </list>
/// <para>Action family (actionable):</para>
/// <list type="bullet">
///   <item><description><c>action_satisfied</c>: the action has been performed
///   (executor=polyphony) or recorded as performed (executor=human).</description></item>
///   <item><description><c>evidence_accepted</c>: evidence artifact for a
///   polyphony-executed action has been promoted to the feature branch.
///   Only emitted when executor=polyphony.</description></item>
/// </list>
/// <para>Terminal (always emitted):</para>
/// <list type="bullet">
///   <item><description><c>item_satisfied</c>: the item is fully complete,
///   including any children. Acts as the unambiguous target for cross-item
///   rollup edges (child <c>item_satisfied</c> → parent <c>item_satisfied</c>).
///   Within an item, every "leaf" requirement (one with no outgoing
///   within-item edges) connects to <c>item_satisfied</c>. For a pure
///   organizational container (empty facet set + decomposable=true),
///   <c>item_satisfied</c> is the only requirement and is satisfied purely
///   by cross-item rollup from its children.</description></item>
/// </list>
/// </remarks>
public static class RequirementKind
{
    public const string PlanAuthored = "plan_authored";
    public const string PlanReviewed = "plan_reviewed";
    public const string PlanPromoted = "plan_promoted";
    public const string ChildrenSeeded = "children_seeded";
    public const string ImplementationMerged = "implementation_merged";
    public const string ActionSatisfied = "action_satisfied";
    public const string EvidenceAccepted = "evidence_accepted";
    public const string ItemSatisfied = "item_satisfied";

    /// <summary>
    /// Returns true if <paramref name="value"/> is one of the canonical requirement kind strings.
    /// </summary>
    public static bool IsValid(string? value) => value is
        PlanAuthored or PlanReviewed or PlanPromoted or ChildrenSeeded or
        ImplementationMerged or ActionSatisfied or EvidenceAccepted or
        ItemSatisfied;
}
