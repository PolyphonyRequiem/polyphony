using Polyphony.Sdlc;

namespace Polyphony;

/// <summary>
/// Output of <c>polyphony edges check</c> — the conflict diagnostic for a
/// run-root's plan-tree subtree, computed by walking the tree, deriving
/// each item's <see cref="RequirementSet"/>, and feeding the lot to
/// <see cref="EdgeGraph.Build"/>. The verb surfaces the resulting
/// <see cref="EdgeGraph.Conflicts"/> list to a human gate (or the apex
/// driver, in a later PR) so a stuck dispatch can be diagnosed without
/// re-running the planner.
///
/// <para>Routing-style verb: always exits 0; consumers branch on
/// <see cref="HasConflicts"/> + <see cref="Conflicts"/>. Errors during
/// the walk (work item not found, derivation failure, cache error)
/// surface via <see cref="Error"/> / <see cref="ErrorCode"/> rather than
/// the process exit code, mirroring the worklist build envelope.</para>
/// </summary>
public sealed record EdgesCheckResult
{
    /// <summary>Run-root work item id supplied positionally (echoed for traceability).</summary>
    public required int WorkItemId { get; init; }

    /// <summary>
    /// Number of items the verb reached during the BFS walk. Zero on
    /// errors that fail before the walk completes (missing root, etc.).
    /// </summary>
    public required int ItemsWalked { get; init; }

    /// <summary>
    /// Total cross-item edges produced by <see cref="EdgeGraph.Build"/>
    /// (sum of all enabled buckets — today only the definitional one).
    /// </summary>
    public required int EdgesTotal { get; init; }

    /// <summary>
    /// Convenience flag — equivalent to <c>Conflicts.Count &gt; 0</c>.
    /// Workflow YAML routes on this rather than re-counting client-side.
    /// </summary>
    public required bool HasConflicts { get; init; }

    /// <summary>
    /// Conflicts detected during merge, in the deterministic order
    /// emitted by <see cref="EdgeGraph.Build"/> (unknown-item entries
    /// first, then cycles smallest-id-first).
    /// </summary>
    public required IReadOnlyList<EdgesCheckConflict> Conflicts { get; init; }

    /// <summary>Operator-facing error message when the conflict report cannot be produced. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>work_item_not_found</c>,
    /// <c>type_unknown</c>, <c>derivation_failed</c>,
    /// <c>cache_error</c>. Null on success.
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// One conflict entry inside <see cref="EdgesCheckResult.Conflicts"/>.
/// Mirrors <see cref="EdgeConflict"/> but with the contributing edges
/// projected through <see cref="CrossItemEdge"/> directly so the JSON
/// shape is stable independent of internal type changes.
/// </summary>
public sealed record EdgesCheckConflict
{
    /// <summary>
    /// Canonical kind string from <see cref="EdgeConflictKind"/> — one
    /// of <c>cycle</c> | <c>threshold_mismatch</c> | <c>unknown_item</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Human-readable diagnostic naming the item ids and requirement
    /// kinds in plain English. Stable across runs for the same input.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Cross-item edges contributing to the conflict. For cycles, the
    /// edges along the cycle in traversal order. For unknown-item
    /// references, the offending edge alone.
    /// </summary>
    public required IReadOnlyList<CrossItemEdge> ContributingEdges { get; init; }
}
