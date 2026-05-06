namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan derive-ancestor-chain</c>. Walks the work-item
/// parent chain from <see cref="ItemId"/> up to (but not past) <see cref="RootId"/>
/// and emits the inputs the Phase 3 plan-PR verbs need:
/// <list type="bullet">
///   <item><c>--root-id</c> for every verb (already known by the workflow).</item>
///   <item><c>--parent-item-id</c> for <c>branch ensure-plan</c>,
///         <c>pr open-plan-pr</c>, and <c>pr merge-plan-pr</c> — required for
///         descendants of descendants; omitted (null) for the root plan and
///         direct children of root.</item>
///   <item><c>--ancestor-ids</c> for <c>pr open-plan-pr</c> — comma-separated
///         chain (immediate parent first), with the literal <c>"root"</c>
///         token used in place of the root work-item id, e.g. <c>"5678,root"</c>
///         for a grandchild. Empty string for the root plan itself.</item>
/// </list>
/// Routing-style verb: always exits 0; consumers branch on the JSON payload.
/// </summary>
public sealed record PlanDeriveAncestorChainResult
{
    /// <summary>Run-root work-item id (echo of the input).</summary>
    public required int RootId { get; init; }

    /// <summary>Item being planned (echo of the input).</summary>
    public required int ItemId { get; init; }

    /// <summary>True when <see cref="ItemId"/> equals <see cref="RootId"/>.</summary>
    public required bool IsRootPlan { get; init; }

    /// <summary>
    /// Immediate plan-tree parent's work-item id, or null when the parent is
    /// implicit (root plan, or direct child of root). Maps directly to the
    /// <c>--parent-item-id</c> flag of the plan-PR verbs (omit when null).
    /// </summary>
    public int? ParentItemId { get; init; }

    /// <summary>
    /// Comma-separated ancestor chain (immediate parent first), with the
    /// literal <c>"root"</c> token used in place of the root work-item id.
    /// Empty string for the root plan. Maps directly to the
    /// <c>--ancestor-ids</c> flag of <c>pr open-plan-pr</c>.
    /// </summary>
    public required string AncestorIds { get; init; }

    /// <summary>
    /// Convenience: same chain as <see cref="AncestorIds"/> but as a list.
    /// Empty for the root plan. Useful for assertions and logs; the verbs
    /// themselves consume <see cref="AncestorIds"/>.
    /// </summary>
    public required IReadOnlyList<string> AncestorChain { get; init; }

    /// <summary>
    /// Plan-tree depth: 0 for root plan, 1 for direct child of root, 2 for
    /// grandchild, etc. Equal to <see cref="AncestorChain"/>.Count.
    /// </summary>
    public required int Depth { get; init; }

    /// <summary>
    /// Operator-facing error message when the chain cannot be derived
    /// (item not found, item not a descendant of root, cycle detected, etc.).
    /// Null on success.
    /// </summary>
    public string? Error { get; init; }
}
