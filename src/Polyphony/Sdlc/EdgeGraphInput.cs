namespace Polyphony.Sdlc;

/// <summary>
/// Per-item input to <see cref="EdgeGraph.Build"/>. Carries the item id,
/// its parent's item id (or 0 for the run root), and its already-derived
/// <see cref="RequirementSet"/>.
/// </summary>
/// <remarks>
/// <para>
/// EdgeGraph deliberately accepts pre-derived requirement sets rather than
/// re-running <see cref="RequirementSetDeriver"/> internally. This keeps
/// the graph layer pure — facet/decomposability lookup belongs at the
/// verb layer, not in the edge graph.
/// </para>
/// </remarks>
/// <param name="ItemId">Positive work item id.</param>
/// <param name="ParentItemId">Parent work item id, or <c>0</c> when this
/// item is the run root. Cross-item edges from the run root do not have
/// an "incoming" parent edge.</param>
/// <param name="RequirementSet">Pre-derived within-item requirements +
/// edges.</param>
public sealed record EdgeGraphInput(
    int ItemId,
    int ParentItemId,
    RequirementSet RequirementSet);
