namespace Polyphony.Sdlc;

/// <summary>
/// Pure derivation of definitional cross-item dependency edges from a
/// parent-child topology. The "definitional" bucket of the three-bucket
/// edge model — hard-wired into the requirement model and not overridable.
/// </summary>
/// <remarks>
/// <para>Two cross-item rules are emitted by this PR (the foundation):</para>
/// <list type="number">
///   <item><description>
///     <b>Children-unblock:</b> when a parent item is plannable +
///     decomposable (so it carries <see cref="RequirementKind.ChildrenSeeded"/>),
///     each child's entry requirement (the within-item requirement with
///     no incoming within-item edges, excluding
///     <see cref="RequirementKind.ItemSatisfied"/>) waits on the parent's
///     <c>children_seeded</c>. This is the only cross-item dependency
///     blocking children from <em>starting</em>.
///   </description></item>
///   <item><description>
///     <b>Terminal rollup:</b> every child's
///     <see cref="RequirementKind.ItemSatisfied"/> becomes a prerequisite
///     of the parent's <c>item_satisfied</c>. This is the only cross-item
///     dependency holding back parent <em>completion</em>.
///   </description></item>
/// </list>
/// <para>
/// Both rules emit edges with
/// <see cref="RequirementEdgeSource.Definitional"/> and threshold
/// <see cref="Disposition.Satisfied"/>.
/// </para>
/// <para>
/// Policy and planner-declared cross-item edges are produced by other
/// derivers (later PRs in the Phase 7 edges arc) and merged into the same
/// flat list at <see cref="EdgeGraph.Build"/> time.
/// </para>
/// </remarks>
public static class CrossItemEdgeDeriver
{
    /// <summary>
    /// Derives the definitional cross-item edges for the given items.
    /// </summary>
    /// <param name="items">All items in scope, keyed by item id. Each
    /// input carries its parent id (or <c>0</c> for the run root) and
    /// its already-derived <see cref="RequirementSet"/>.</param>
    /// <returns>Flat list of cross-item edges, in deterministic order
    /// (children-unblock first, then terminal-rollup; within each
    /// category, by parent id ascending then child id ascending).</returns>
    public static IReadOnlyList<CrossItemEdge> DeriveDefinitional(
        IReadOnlyDictionary<int, EdgeGraphInput> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var edges = new List<CrossItemEdge>();

        // Group items by parent id for deterministic per-parent iteration.
        // ParentItemId = 0 means "no parent in scope" (typically the run root).
        var childrenByParent = new SortedDictionary<int, List<EdgeGraphInput>>();
        foreach (var item in items.Values)
        {
            if (item.ParentItemId == 0) continue;
            if (!childrenByParent.TryGetValue(item.ParentItemId, out var siblings))
            {
                siblings = new List<EdgeGraphInput>();
                childrenByParent[item.ParentItemId] = siblings;
            }
            siblings.Add(item);
        }

        // Sort children for determinism.
        foreach (var siblings in childrenByParent.Values)
        {
            siblings.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
        }

        // Rule 1: children-unblock. Emit only when the parent has
        // children_seeded as a within-item requirement (i.e. parent is
        // plannable + decomposable). For pure containers (decomposable
        // but not plannable), no children_seeded exists and children
        // are unblocked from the start.
        foreach (var (parentId, siblings) in childrenByParent)
        {
            if (!items.TryGetValue(parentId, out var parent)) continue;
            if (!HasRequirement(parent.RequirementSet, RequirementKind.ChildrenSeeded)) continue;

            foreach (var child in siblings)
            {
                foreach (var entry in FindEntryRequirements(child.RequirementSet))
                {
                    edges.Add(new CrossItemEdge(
                        PrerequisiteItemId: parentId,
                        PrerequisiteKind: RequirementKind.ChildrenSeeded,
                        DependentItemId: child.ItemId,
                        DependentKind: entry,
                        RequiredDisposition: Disposition.Satisfied,
                        Source: RequirementEdgeSource.Definitional));
                }
            }
        }

        // Rule 2: terminal rollup. Always emit when both parent and child
        // are in scope. Both ends use ItemSatisfied (every item carries it
        // post-RequirementSetDeriver).
        foreach (var (parentId, siblings) in childrenByParent)
        {
            if (!items.TryGetValue(parentId, out var parent)) continue;
            if (!HasRequirement(parent.RequirementSet, RequirementKind.ItemSatisfied)) continue;

            foreach (var child in siblings)
            {
                if (!HasRequirement(child.RequirementSet, RequirementKind.ItemSatisfied)) continue;

                edges.Add(new CrossItemEdge(
                    PrerequisiteItemId: child.ItemId,
                    PrerequisiteKind: RequirementKind.ItemSatisfied,
                    DependentItemId: parentId,
                    DependentKind: RequirementKind.ItemSatisfied,
                    RequiredDisposition: Disposition.Satisfied,
                    Source: RequirementEdgeSource.Definitional));
            }
        }

        return edges;
    }

    private static bool HasRequirement(RequirementSet set, string kind)
    {
        foreach (var req in set.Items)
        {
            if (req.Kind == kind) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the kinds of requirements with no incoming within-item edges,
    /// EXCLUDING <see cref="RequirementKind.ItemSatisfied"/>. These are
    /// the "entry" requirements — what must wait on the parent's
    /// <c>children_seeded</c>.
    /// </summary>
    /// <remarks>
    /// For an empty pure container (no facets, decomposable=true), the
    /// only requirement is <c>item_satisfied</c>, so this returns an
    /// empty list — there is nothing to unblock at the entry, only the
    /// terminal rollup matters.
    /// </remarks>
    private static IReadOnlyList<string> FindEntryRequirements(RequirementSet set)
    {
        var hasIncoming = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in set.Edges)
        {
            hasIncoming.Add(edge.DependentKind);
        }

        var entries = new List<string>();
        foreach (var req in set.Items)
        {
            if (req.Kind == RequirementKind.ItemSatisfied) continue;
            if (!hasIncoming.Contains(req.Kind))
            {
                entries.Add(req.Kind);
            }
        }
        return entries;
    }
}
