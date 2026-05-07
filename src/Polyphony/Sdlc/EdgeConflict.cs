namespace Polyphony.Sdlc;

/// <summary>
/// A conflict detected during <see cref="EdgeGraph"/> construction that
/// prevents producing a topological ordering. Surfaces to the human
/// gate via <c>polyphony edges check</c> in a later PR.
/// </summary>
/// <remarks>
/// <para>
/// Conflict kinds (canonical strings — match against <see cref="EdgeConflictKind"/>):
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>cycle</c> — the merged directed graph contains a cycle.
///     <see cref="ContributingEdges"/> lists the edges along the cycle.
///   </description></item>
///   <item><description>
///     <c>threshold_mismatch</c> — a planner-declared edge tries to
///     <em>relax</em> a definitional or policy threshold (only
///     tightening is allowed across sources). Reserved for a later PR
///     in the Phase 7 edges arc.
///   </description></item>
///   <item><description>
///     <c>unknown_item</c> — a planner-declared edge names a
///     <c>prerequisite_item</c> that is not in the worklist. Hard
///     conflict (planner explicitly named it; if it is gone, the
///     planner's reasoning is invalidated). Reserved for a later PR
///     in the Phase 7 edges arc.
///   </description></item>
/// </list>
/// <para>
/// Conflict detection itself lands in PR #2 of the edges arc. PR #1
/// emits the type and a no-op <see cref="EdgeGraph.Conflicts"/> list so
/// downstream PRs can populate it without re-shaping the graph API.
/// </para>
/// </remarks>
/// <param name="Kind">Canonical conflict kind string.</param>
/// <param name="ContributingEdges">All cross-item edges that contribute
/// to the conflict. For cycles, the edges along the cycle in traversal
/// order. For threshold mismatches, the conflicting edges from each
/// source. For unknown-item references, the planner edge.</param>
/// <param name="Description">Human-readable diagnostic for the conflict
/// gate to render. Should name the item ids and requirement kinds in
/// plain English.</param>
public sealed record EdgeConflict(
    string Kind,
    IReadOnlyList<CrossItemEdge> ContributingEdges,
    string Description);

/// <summary>
/// Canonical conflict kind string constants for <see cref="EdgeConflict.Kind"/>.
/// </summary>
public static class EdgeConflictKind
{
    public const string Cycle = "cycle";
    public const string ThresholdMismatch = "threshold_mismatch";
    public const string UnknownItem = "unknown_item";

    /// <summary>
    /// Returns true if <paramref name="value"/> is one of the canonical conflict kind strings.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is Cycle or ThresholdMismatch or UnknownItem;
}
