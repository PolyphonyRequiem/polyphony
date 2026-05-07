namespace Polyphony.Sdlc;

/// <summary>
/// A dependency edge between requirements on TWO different work items in
/// the same run. Cross-item analogue of <see cref="RequirementEdge"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cross-item edges are computed at <see cref="EdgeGraph"/> construction
/// time by merging up to three buckets:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>definitional</c> — emitted by <see cref="CrossItemEdgeDeriver"/>
///     from the parent-child topology (e.g. parent
///     <c>children_seeded</c> blocks each child's entry requirement;
///     child <c>item_satisfied</c> blocks parent <c>item_satisfied</c>).
///   </description></item>
///   <item><description>
///     <c>policy</c> — declared in <c>process-config.yaml</c> as
///     defaults scoped by item type or facet. (Reserved for a later PR
///     in the Phase 7 edges arc.)
///   </description></item>
///   <item><description>
///     <c>planner_declared</c> — emitted by the planner per item,
///     surfaced in the plan document, reviewed via the plan PR.
///     (Reserved for a later PR in the Phase 7 edges arc.)
///   </description></item>
/// </list>
/// <para>
/// Threshold semantics mirror <see cref="RequirementEdge.RequiredDisposition"/>:
/// the dependent's containing requirement becomes <c>Ready</c> only when the
/// prerequisite reaches the named <see cref="RequiredDisposition"/>.
/// </para>
/// </remarks>
/// <param name="PrerequisiteItemId">Work item id whose
/// <paramref name="PrerequisiteKind"/> requirement must reach the threshold.</param>
/// <param name="PrerequisiteKind">Requirement kind on the prerequisite item.</param>
/// <param name="DependentItemId">Work item id whose
/// <paramref name="DependentKind"/> requirement waits on the prerequisite.</param>
/// <param name="DependentKind">Requirement kind on the dependent item.</param>
/// <param name="RequiredDisposition">Disposition the prerequisite must
/// reach to release the dependent. Almost always
/// <see cref="Disposition.Satisfied"/> for definitional edges; later
/// buckets may emit looser thresholds for plan-gate granularity.</param>
/// <param name="Source">Provenance from <see cref="RequirementEdgeSource"/>.</param>
public sealed record CrossItemEdge(
    int PrerequisiteItemId,
    string PrerequisiteKind,
    int DependentItemId,
    string DependentKind,
    string RequiredDisposition,
    string Source);
