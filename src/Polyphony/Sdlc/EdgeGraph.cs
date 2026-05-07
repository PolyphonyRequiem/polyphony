namespace Polyphony.Sdlc;

/// <summary>
/// The merged dependency graph for a run-root's worklist. Owns:
/// <list type="bullet">
///   <item>The set of items in scope, with their pre-derived
///   <see cref="RequirementSet"/>s (within-item requirements + edges).</item>
///   <item>A flat list of <see cref="CrossItemEdge"/>s computed by merging
///   the three buckets (definitional / policy / planner-declared).</item>
///   <item>A list of <see cref="EdgeConflict"/>s detected during merge.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// PR #1 of the Phase 7 edges arc ships only the <c>definitional</c>
/// bucket via <see cref="CrossItemEdgeDeriver.DeriveDefinitional"/>.
/// Policy and planner-declared buckets, plus cycle detection and
/// threshold-mismatch detection, land in subsequent PRs.
/// </para>
/// <para>
/// EdgeGraph is an immutable value type — once built, it is safe to share
/// across threads. The graph carries no I/O dependencies and no platform
/// state; it is purely derived from its inputs.
/// </para>
/// </remarks>
public sealed class EdgeGraph
{
    /// <summary>
    /// Pre-derived requirements per item, keyed by item id. The graph holds
    /// the originals — callers should not mutate the underlying records.
    /// </summary>
    public IReadOnlyDictionary<int, RequirementSet> ItemRequirements { get; }

    /// <summary>
    /// Cross-item edges after merging all enabled buckets. Stable order
    /// matches <see cref="CrossItemEdgeDeriver.DeriveDefinitional"/>'s
    /// emission order in PR #1; subsequent PRs that add buckets will
    /// extend the list while preserving prior ordering.
    /// </summary>
    public IReadOnlyList<CrossItemEdge> Edges { get; }

    /// <summary>
    /// Conflicts detected during merge. Empty list on a clean build.
    /// PR #1 always produces an empty list (no detection logic yet);
    /// PR #2 populates it with cycle and unknown-item entries.
    /// </summary>
    public IReadOnlyList<EdgeConflict> Conflicts { get; }

    private EdgeGraph(
        IReadOnlyDictionary<int, RequirementSet> itemRequirements,
        IReadOnlyList<CrossItemEdge> edges,
        IReadOnlyList<EdgeConflict> conflicts)
    {
        ItemRequirements = itemRequirements;
        Edges = edges;
        Conflicts = conflicts;
    }

    /// <summary>
    /// Builds the merged edge graph from per-item inputs.
    /// </summary>
    /// <param name="items">All items in scope. Must contain at least one
    /// item. Each item carries its parent id and pre-derived
    /// <see cref="RequirementSet"/>.</param>
    /// <returns>An immutable <see cref="EdgeGraph"/>. Always succeeds —
    /// errors surface via <see cref="Conflicts"/> rather than exceptions
    /// so the conflict gate has a renderable diagnostic.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="items"/>
    /// is empty or contains a duplicate item id.</exception>
    /// <exception cref="ArgumentNullException">Thrown when
    /// <paramref name="items"/> is <c>null</c>.</exception>
    public static EdgeGraph Build(IReadOnlyList<EdgeGraphInput> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("EdgeGraph.Build requires at least one item.", nameof(items));
        }

        var itemMap = new Dictionary<int, EdgeGraphInput>(items.Count);
        foreach (var item in items)
        {
            if (!itemMap.TryAdd(item.ItemId, item))
            {
                throw new ArgumentException(
                    $"Duplicate item id {item.ItemId} in EdgeGraph.Build inputs.",
                    nameof(items));
            }
        }

        var crossItemEdges = CrossItemEdgeDeriver.DeriveDefinitional(itemMap);

        // PR #1: no conflict detection yet — empty list. Cycle and
        // unknown-item detection land in PR #2 of the edges arc.
        var conflicts = Array.Empty<EdgeConflict>();

        var requirements = new Dictionary<int, RequirementSet>(itemMap.Count);
        foreach (var (id, input) in itemMap)
        {
            requirements[id] = input.RequirementSet;
        }

        return new EdgeGraph(requirements, crossItemEdges, conflicts);
    }

    /// <summary>
    /// Computes the topological wave grouping over the union graph
    /// (within-item edges ∪ cross-item edges). An item is "ready for
    /// dispatch" when every prerequisite of every entry requirement
    /// (the within-item requirements with no incoming within-item edges,
    /// excluding <see cref="RequirementKind.ItemSatisfied"/>) has been
    /// satisfied.
    /// </summary>
    /// <returns>Ordered waves. Wave 0 contains items with no inbound
    /// cross-item edges into their entry requirements. Wave N contains
    /// items first becoming dispatchable in this round. Items within a
    /// wave are sorted by id ascending.</returns>
    /// <exception cref="InvalidOperationException">Thrown when
    /// <see cref="Conflicts"/> is non-empty. The conflict gate must
    /// resolve all conflicts before a topological ordering exists.</exception>
    public IReadOnlyList<EdgeGraphWave> ToWaves()
    {
        if (Conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot produce waves while {Conflicts.Count} conflict(s) remain. " +
                "Resolve conflicts via the human gate before requesting dispatch order.");
        }

        // Compute per-item entry requirements (the things that gate dispatch).
        var entryRequirementsByItem = new Dictionary<int, IReadOnlyList<string>>(ItemRequirements.Count);
        foreach (var (itemId, set) in ItemRequirements)
        {
            entryRequirementsByItem[itemId] = ComputeEntryRequirements(set);
        }

        // Per-item, count cross-item edges that target the item's entry
        // requirements. An item is dispatchable in wave N when every such
        // prerequisite is in waves 0..N-1.
        var pendingPrereqs = new Dictionary<int, HashSet<int>>(ItemRequirements.Count);
        foreach (var itemId in ItemRequirements.Keys)
        {
            pendingPrereqs[itemId] = new HashSet<int>();
        }

        foreach (var edge in Edges)
        {
            // Only edges targeting the dependent item's ENTRY requirements
            // gate dispatch. Edges targeting non-entry requirements (e.g.
            // child.ItemSatisfied → parent.ItemSatisfied) gate completion,
            // not start.
            if (!entryRequirementsByItem.TryGetValue(edge.DependentItemId, out var entries)) continue;
            if (!entries.Contains(edge.DependentKind)) continue;

            pendingPrereqs[edge.DependentItemId].Add(edge.PrerequisiteItemId);
        }

        // Kahn's algorithm at the item granularity.
        var waves = new List<EdgeGraphWave>();
        var dispatched = new HashSet<int>();
        var waveIndex = 0;

        while (dispatched.Count < ItemRequirements.Count)
        {
            var ready = new List<int>();
            foreach (var itemId in ItemRequirements.Keys)
            {
                if (dispatched.Contains(itemId)) continue;
                if (pendingPrereqs[itemId].Count == 0) ready.Add(itemId);
            }

            if (ready.Count == 0)
            {
                // Should be unreachable when Conflicts is empty (cycle would
                // have been detected by PR #2); guard for forward compatibility.
                throw new InvalidOperationException(
                    "EdgeGraph.ToWaves stalled with no dispatchable items remaining. " +
                    "This indicates an undetected cycle in the cross-item edges.");
            }

            ready.Sort();
            waves.Add(new EdgeGraphWave(waveIndex, ready));

            foreach (var itemId in ready)
            {
                dispatched.Add(itemId);
            }

            // Release downstream items: remove dispatched items from the
            // pending-prereq sets of their dependents.
            foreach (var pending in pendingPrereqs.Values)
            {
                foreach (var itemId in ready)
                {
                    pending.Remove(itemId);
                }
            }

            waveIndex++;
        }

        return waves;
    }

    /// <summary>
    /// Returns the kinds of requirements with no incoming within-item
    /// edges, EXCLUDING <see cref="RequirementKind.ItemSatisfied"/>.
    /// These are the "entry" requirements — the within-item gates that
    /// determine when an item can be dispatched.
    /// </summary>
    private static IReadOnlyList<string> ComputeEntryRequirements(RequirementSet set)
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
