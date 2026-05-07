namespace Polyphony.Sdlc;

/// <summary>
/// Pure derivation of the requirement set + within-item edges from an item's
/// facets, decomposability, facet order, and (for actionable) executor.
/// </summary>
/// <remarks>
/// <para>
/// Foundation of the EdgeGraph-based state model. It contains no I/O and no
/// dependency on configuration objects — callers pass already-resolved inputs.
/// That keeps the deriver trivially testable and lets the verb layer own the
/// lookup of facets/decomposability/etc. from the work-item repository and
/// process config.
/// </para>
/// <para>
/// All emitted requirements have <see cref="Disposition.Needed"/> initially.
/// Computing transitions to <c>Ready</c>/<c>Fulfilling</c>/<c>Satisfied</c> is
/// the consumer's job (it requires inspecting current item state, child state,
/// PR state, etc.).
/// </para>
/// <para>
/// All emitted edges have <see cref="RequirementEdgeSource.Definitional"/>.
/// Policy and planner-declared edges are layered in by later phases.
/// </para>
/// </remarks>
public static class RequirementSetDeriver
{
    /// <summary>Default canonical facet order. Plannable always fires first
    /// when present; for the remaining two, action-then-implementation is the
    /// glossary default unless the planner declares otherwise.</summary>
    public static readonly IReadOnlyList<string> DefaultFacetOrder =
        [Facet.Plannable, Facet.Actionable, Facet.Implementable];

    /// <summary>
    /// Derives the requirement set + within-item edges for an item.
    /// </summary>
    /// <param name="facets">Facets the item carries (may be empty).</param>
    /// <param name="decomposable">Whether the item is permitted to have children.
    /// MUST be supplied explicitly — there is no safe proxy for this. An empty
    /// facet set with <paramref name="decomposable"/>=false is invalid (the item
    /// has neither own work nor sub-work).</param>
    /// <param name="facetOrder">Optional planner-declared ordering of the
    /// non-plannable facets. Required when the item has BOTH actionable and
    /// implementable; ignored otherwise (a warning is emitted if supplied
    /// pointlessly). When <c>null</c> and the item has at most one non-plannable
    /// facet, the order is unambiguous and no input is needed.</param>
    /// <param name="actionableExecutor">Required when <c>actionable</c> is in
    /// <paramref name="facets"/>; must be <c>null</c> otherwise. Determines
    /// whether <see cref="RequirementKind.EvidenceAccepted"/> is emitted.</param>
    /// <returns>A derivation result. Check <see cref="RequirementSetDerivation.IsValid"/>.</returns>
    public static RequirementSetDerivation Derive(
        IReadOnlyList<string> facets,
        bool decomposable,
        IReadOnlyList<string>? facetOrder = null,
        string? actionableExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(facets);

        var errors = new List<string>();
        var warnings = new List<string>();

        // Validate facet membership and detect duplicates.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in facets)
        {
            if (!Facet.IsValid(f))
            {
                errors.Add($"Unknown facet '{f}'. Allowed: {Facet.Plannable}, {Facet.Actionable}, {Facet.Implementable}.");
                continue;
            }
            if (!seen.Add(f))
            {
                errors.Add($"Duplicate facet '{f}'.");
            }
        }

        var hasPlannable = seen.Contains(Facet.Plannable);
        var hasActionable = seen.Contains(Facet.Actionable);
        var hasImplementable = seen.Contains(Facet.Implementable);

        // Empty + non-decomposable: an item with neither own work nor sub-work
        // is meaningless. Empty + decomposable is the pure organizational
        // container (zero own-work requirements; satisfaction is cross-item).
        if (seen.Count == 0 && !decomposable)
        {
            errors.Add("An item with no facets must be decomposable; otherwise it has neither own work nor sub-work.");
        }

        // Executor invariants tied to the actionable facet.
        if (hasActionable)
        {
            if (actionableExecutor is null)
            {
                errors.Add($"Actionable facet requires an executor. Allowed: {ActionableExecutor.Polyphony}, {ActionableExecutor.Human}.");
            }
            else if (!ActionableExecutor.IsValid(actionableExecutor))
            {
                errors.Add($"Unknown actionable executor '{actionableExecutor}'. Allowed: {ActionableExecutor.Polyphony}, {ActionableExecutor.Human}.");
            }
        }
        else if (actionableExecutor is not null)
        {
            errors.Add("Actionable executor supplied but item is not actionable.");
        }

        // facetOrder validation — only meaningful when both actionable and
        // implementable are present; required in that case to avoid silently
        // guessing a planner-declared decision.
        IReadOnlyList<string> resolvedOrder;
        if (hasActionable && hasImplementable)
        {
            if (facetOrder is null || facetOrder.Count == 0)
            {
                errors.Add($"facet_order is required when an item has both '{Facet.Actionable}' and '{Facet.Implementable}'. The planner must declare the order — there is no safe default.");
                resolvedOrder = [];
            }
            else
            {
                resolvedOrder = ValidateExplicitFacetOrder(facetOrder, seen, errors);
            }
        }
        else
        {
            if (facetOrder is not null && facetOrder.Count > 0)
            {
                warnings.Add("facet_order supplied but the item has fewer than two non-plannable facets; ordering is unambiguous and the input is ignored.");
            }
            resolvedOrder = [];
        }

        if (errors.Count > 0)
        {
            return new RequirementSetDerivation(Set: null, errors, warnings);
        }

        var requirements = new List<Requirement>();
        var edges = new List<RequirementEdge>();

        // Plannable family.
        if (hasPlannable)
        {
            requirements.Add(NewRequirement(RequirementKind.PlanAuthored));
            requirements.Add(NewRequirement(RequirementKind.PlanReviewed));
            requirements.Add(NewRequirement(RequirementKind.PlanPromoted));
            edges.Add(DefinitionalEdge(RequirementKind.PlanAuthored, RequirementKind.PlanReviewed));
            edges.Add(DefinitionalEdge(RequirementKind.PlanReviewed, RequirementKind.PlanPromoted));
        }

        // Decomposition. Only meaningful when the item is plannable AND
        // decomposable: you need a promoted plan to know what to seed.
        // Empty-facets-but-decomposable (pure container) emits no own-work
        // requirements; child satisfaction is a cross-item concern handled
        // by later phases.
        var hasChildrenSeeded = decomposable && hasPlannable;
        if (hasChildrenSeeded)
        {
            requirements.Add(NewRequirement(RequirementKind.ChildrenSeeded));
            edges.Add(DefinitionalEdge(RequirementKind.PlanPromoted, RequirementKind.ChildrenSeeded));
        }

        // Implementation family.
        if (hasImplementable)
        {
            requirements.Add(NewRequirement(RequirementKind.ImplementationMerged));
        }

        // Action family.
        if (hasActionable)
        {
            requirements.Add(NewRequirement(RequirementKind.ActionSatisfied));
            if (actionableExecutor == ActionableExecutor.Polyphony)
            {
                requirements.Add(NewRequirement(RequirementKind.EvidenceAccepted));
                edges.Add(DefinitionalEdge(RequirementKind.ActionSatisfied, RequirementKind.EvidenceAccepted));
            }
        }

        // Cross-facet within-item gates.
        // (1) The first non-plannable facet's entry requirement waits on the
        //     plannable family completing (children_seeded if present, else plan_promoted).
        if (hasPlannable && (hasActionable || hasImplementable))
        {
            var planExitKind = hasChildrenSeeded
                ? RequirementKind.ChildrenSeeded
                : RequirementKind.PlanPromoted;

            var firstNonPlannable = FirstNonPlannableFacet(resolvedOrder, hasActionable, hasImplementable);
            var entryKind = EntryRequirementForFacet(firstNonPlannable);
            edges.Add(DefinitionalEdge(planExitKind, entryKind));
        }

        // (2) Within-item facet order between actionable and implementable.
        if (hasActionable && hasImplementable)
        {
            var firstNonPlannable = FirstNonPlannableFacet(resolvedOrder, hasActionable, hasImplementable);
            if (firstNonPlannable == Facet.Actionable)
            {
                // [a, i]: implementation waits for action_satisfied.
                edges.Add(DefinitionalEdge(RequirementKind.ActionSatisfied, RequirementKind.ImplementationMerged));
            }
            else
            {
                // [i, a]: action waits for implementation_merged.
                edges.Add(DefinitionalEdge(RequirementKind.ImplementationMerged, RequirementKind.ActionSatisfied));
            }
        }

        // (3) Synthetic terminal: every item gets `item_satisfied` as the
        //     unambiguous target for cross-item rollup edges (child terminal
        //     → parent terminal). Within an item, every "leaf" requirement
        //     (no outgoing within-item edges) connects to item_satisfied.
        //
        //     For a pure organizational container (empty facet set +
        //     decomposable=true), item_satisfied is the only requirement;
        //     its prerequisites are filled in entirely by cross-item rollup.
        requirements.Add(NewRequirement(RequirementKind.ItemSatisfied));
        var leafKinds = ComputeLeafKinds(requirements, edges);
        foreach (var leaf in leafKinds)
        {
            edges.Add(DefinitionalEdge(leaf, RequirementKind.ItemSatisfied));
        }

        var set = new RequirementSet(requirements, edges);
        return new RequirementSetDerivation(set, errors, warnings);
    }

    /// <summary>
    /// Returns the kinds of requirements that have no outgoing within-item
    /// edges, EXCLUDING <see cref="RequirementKind.ItemSatisfied"/> itself.
    /// These are the "leaf" requirements that need to flow into the synthetic
    /// terminal. Order is the requirement-list emission order so the resulting
    /// edges are deterministic for golden-output tests.
    /// </summary>
    private static IReadOnlyList<string> ComputeLeafKinds(
        IReadOnlyList<Requirement> requirements,
        IReadOnlyList<RequirementEdge> edges)
    {
        var hasOutgoing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            hasOutgoing.Add(edge.PrerequisiteKind);
        }

        var leaves = new List<string>();
        foreach (var req in requirements)
        {
            if (req.Kind == RequirementKind.ItemSatisfied) continue;
            if (!hasOutgoing.Contains(req.Kind))
            {
                leaves.Add(req.Kind);
            }
        }
        return leaves;
    }

    private static Requirement NewRequirement(string kind) =>
        new(kind, Disposition.Needed, AcceptanceCriteria: null);

    private static RequirementEdge DefinitionalEdge(string prerequisite, string dependent) =>
        new(prerequisite, dependent, Disposition.Satisfied, RequirementEdgeSource.Definitional);

    private static IReadOnlyList<string> ValidateExplicitFacetOrder(
        IReadOnlyList<string> order,
        HashSet<string> presentFacets,
        List<string> errors)
    {
        var seenInOrder = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in order)
        {
            if (!Facet.IsValid(f))
            {
                errors.Add($"facet_order contains unknown facet '{f}'.");
                continue;
            }
            if (!seenInOrder.Add(f))
            {
                errors.Add($"facet_order contains duplicate '{f}'.");
                continue;
            }
            if (!presentFacets.Contains(f))
            {
                errors.Add($"facet_order references facet '{f}' that is not in the item's facet set.");
            }
        }

        // Validate that the two non-plannable facets are both present in the order.
        var hasA = seenInOrder.Contains(Facet.Actionable);
        var hasI = seenInOrder.Contains(Facet.Implementable);
        if (!hasA || !hasI)
        {
            errors.Add($"facet_order must include both '{Facet.Actionable}' and '{Facet.Implementable}' when both are present on the item.");
        }

        return order;
    }

    private static string FirstNonPlannableFacet(
        IReadOnlyList<string> resolvedOrder,
        bool hasActionable,
        bool hasImplementable)
    {
        // When both are present, the resolved order is authoritative.
        if (hasActionable && hasImplementable)
        {
            foreach (var f in resolvedOrder)
            {
                if (f == Facet.Actionable || f == Facet.Implementable)
                {
                    return f;
                }
            }
            // Should be unreachable — validation above guarantees presence.
            return Facet.Actionable;
        }

        // When only one is present, that one is "first" trivially.
        return hasActionable ? Facet.Actionable : Facet.Implementable;
    }

    private static string EntryRequirementForFacet(string facet) => facet switch
    {
        Facet.Actionable => RequirementKind.ActionSatisfied,
        Facet.Implementable => RequirementKind.ImplementationMerged,
        _ => throw new ArgumentOutOfRangeException(nameof(facet), facet, "No entry requirement defined for facet."),
    };
}
