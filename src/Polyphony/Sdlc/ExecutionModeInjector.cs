namespace Polyphony.Sdlc;

/// <summary>
/// Injects synthetic within-item edges into a <see cref="RequirementSet"/>
/// based on the resolved <c>execution_mode</c> knob (Phase 7 edges PR #5,
/// closing the arc opened by PRs #1, #2, and #4).
/// </summary>
/// <remarks>
/// <para>
/// This is the layer that translates the configuration value resolved by
/// <see cref="RequirementInputResolver"/> into a concrete edge addition
/// against an already-derived <see cref="RequirementSet"/>. The
/// <see cref="RequirementSetDeriver"/> itself stays mode-agnostic; callers
/// compose <c>Inject(Derive(...), resolvedInputs.ExecutionMode)</c> when
/// they want the mode applied. PR #5 wires only the injector — retrofit
/// of the existing <c>state next-ready</c> and <c>requirements derive</c>
/// callers happens later in Phase 7.
/// </para>
/// <para>
/// Modes:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="ExecutionMode.Parallel"/> (default) — no-op. The same
///     <see cref="RequirementSet"/> instance is returned; no allocation.
///   </description></item>
///   <item><description>
///     <see cref="ExecutionMode.PlanThenImplement"/> — when the set carries
///     BOTH <see cref="RequirementKind.PlanPromoted"/> AND
///     <see cref="RequirementKind.ImplementationMerged"/>, a single edge
///     <c>plan_promoted → implementation_merged</c> at threshold
///     <see cref="Disposition.Satisfied"/> is appended. If either kind is
///     absent the mode is irrelevant and no edge is added (return is the
///     same instance). If the edge is already present (e.g. the deriver
///     emitted it because the item is plannable+implementable without
///     intervening <c>children_seeded</c>), no duplicate is appended —
///     the operation is idempotent.
///   </description></item>
/// </list>
/// <para>
/// Unknown mode strings are treated as <see cref="ExecutionMode.Parallel"/>
/// (no-op). Validation of unknown values is the responsibility of the
/// config loader (<c>ConfigValidator</c> rule V-19, landed in PR #126);
/// the injector fails open at runtime so a stale or hand-built
/// <see cref="ResolvedRequirementInputs"/> never throws here.
/// </para>
/// <para>
/// <b>Source attribution:</b> the injected edge carries
/// <see cref="RequirementEdgeSource.Definitional"/>. The execution-mode
/// knob is a hard-wired transformation from a typed configuration value
/// to a fixed edge shape — it is not a per-instance policy or a
/// planner-declared dependency. Adding a fourth source constant
/// (<c>execution_mode</c>) was considered and rejected to keep the
/// taxonomy stable; the rationale is that downstream consumers care
/// about <i>whether</i> the edge can be overridden (no, it cannot —
/// just like every other definitional edge) rather than about the
/// proximate cause of its emission. See the PR body for the full trade-off
/// discussion.
/// </para>
/// </remarks>
public static class ExecutionModeInjector
{
    /// <summary>
    /// Returns <paramref name="set"/> with mode-driven synthetic edges
    /// applied. See the type-level remarks for the per-mode behavior
    /// contract. Pure function — no I/O, no state, no exceptions on
    /// unknown <paramref name="executionMode"/>.
    /// </summary>
    /// <param name="set">The derived requirement set to inject into.</param>
    /// <param name="executionMode">The resolved execution mode, typically
    /// <see cref="ResolvedRequirementInputs.ExecutionMode"/>. Anything
    /// other than <see cref="ExecutionMode.PlanThenImplement"/> is treated
    /// as a no-op.</param>
    /// <returns>The original set when the mode is a no-op (same reference);
    /// a new set with the appended edge otherwise.</returns>
    public static RequirementSet Inject(
        RequirementSet set,
        string executionMode)
    {
        ArgumentNullException.ThrowIfNull(set);

        if (executionMode != ExecutionMode.PlanThenImplement)
        {
            return set;
        }

        var hasPlanPromoted = false;
        var hasImplementationMerged = false;
        foreach (var item in set.Items)
        {
            if (item.Kind == RequirementKind.PlanPromoted) hasPlanPromoted = true;
            else if (item.Kind == RequirementKind.ImplementationMerged) hasImplementationMerged = true;
            if (hasPlanPromoted && hasImplementationMerged) break;
        }

        if (!hasPlanPromoted || !hasImplementationMerged)
        {
            return set;
        }

        foreach (var edge in set.Edges)
        {
            if (edge.PrerequisiteKind == RequirementKind.PlanPromoted &&
                edge.DependentKind == RequirementKind.ImplementationMerged)
            {
                return set;
            }
        }

        var newEdges = new List<RequirementEdge>(set.Edges.Count + 1);
        newEdges.AddRange(set.Edges);
        newEdges.Add(new RequirementEdge(
            PrerequisiteKind: RequirementKind.PlanPromoted,
            DependentKind: RequirementKind.ImplementationMerged,
            RequiredDisposition: Disposition.Satisfied,
            Source: RequirementEdgeSource.Definitional));

        return new RequirementSet(set.Items, newEdges);
    }
}
