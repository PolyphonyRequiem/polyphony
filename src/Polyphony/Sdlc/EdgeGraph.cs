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
/// PR #1 of the Phase 7 edges arc shipped only the <c>definitional</c>
/// bucket via <see cref="CrossItemEdgeDeriver.DeriveDefinitional"/>.
/// PR #2 adds <c>cycle</c> and <c>unknown_item</c> conflict detection
/// (run unconditionally over the merged edge list inside
/// <see cref="Build"/>). Policy and planner-declared buckets, plus
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
    /// As of PR #2 of the edges arc this list is populated with
    /// <see cref="EdgeConflictKind.Cycle"/> and
    /// <see cref="EdgeConflictKind.UnknownItem"/> entries.
    /// Threshold-mismatch detection lands with the planner-declared
    /// bucket in a subsequent PR.
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

        var itemMap = BuildItemMap(items);
        var crossItemEdges = CrossItemEdgeDeriver.DeriveDefinitional(itemMap);
        var conflicts = DetectConflicts(itemMap, crossItemEdges);
        var requirements = BuildRequirementsMap(itemMap);

        return new EdgeGraph(requirements, crossItemEdges, conflicts);
    }

    /// <summary>
    /// Internal test seam — builds an <see cref="EdgeGraph"/> from a
    /// hand-crafted edge list, bypassing <see cref="CrossItemEdgeDeriver"/>.
    /// </summary>
    /// <remarks>
    /// PR #1's definitional rules cannot produce a cycle (children-unblock
    /// goes parent→child, terminal-rollup goes child→parent — disjoint
    /// directions on the same parent-child pair). To exercise cycle
    /// detection (and the unknown-item case until policy / planner-declared
    /// buckets land in later PRs) tests inject crafted edges via this seam.
    /// </remarks>
    internal static EdgeGraph BuildFromEdges(
        IReadOnlyList<EdgeGraphInput> items,
        IReadOnlyList<CrossItemEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(edges);
        if (items.Count == 0)
        {
            throw new ArgumentException("EdgeGraph.BuildFromEdges requires at least one item.", nameof(items));
        }

        var itemMap = BuildItemMap(items);
        var conflicts = DetectConflicts(itemMap, edges);
        var requirements = BuildRequirementsMap(itemMap);

        return new EdgeGraph(requirements, edges, conflicts);
    }

    private static Dictionary<int, EdgeGraphInput> BuildItemMap(IReadOnlyList<EdgeGraphInput> items)
    {
        var map = new Dictionary<int, EdgeGraphInput>(items.Count);
        foreach (var item in items)
        {
            if (!map.TryAdd(item.ItemId, item))
            {
                throw new ArgumentException(
                    $"Duplicate item id {item.ItemId} in EdgeGraph.Build inputs.",
                    nameof(items));
            }
        }
        return map;
    }

    private static Dictionary<int, RequirementSet> BuildRequirementsMap(IReadOnlyDictionary<int, EdgeGraphInput> itemMap)
    {
        var requirements = new Dictionary<int, RequirementSet>(itemMap.Count);
        foreach (var (id, input) in itemMap)
        {
            requirements[id] = input.RequirementSet;
        }
        return requirements;
    }

    /// <summary>
    /// Runs the conflict-detection passes over the merged edge list.
    /// Order in the returned list: unknown-item conflicts first (in the
    /// edges' deterministic input order), then cycles (in the order they
    /// are first reached by DFS, which — given id-ascending DFS roots —
    /// orders cycles by smallest item id).
    /// </summary>
    private static IReadOnlyList<EdgeConflict> DetectConflicts(
        IReadOnlyDictionary<int, EdgeGraphInput> itemMap,
        IReadOnlyList<CrossItemEdge> edges)
    {
        var conflicts = new List<EdgeConflict>();

        // Pass 1: unknown-item. Iterate edges in their existing order so
        // ordering is stable across calls. PR #1's definitional deriver
        // never produces unknown-endpoint edges (it iterates only known
        // items), so this pass is a no-op until policy / planner-declared
        // buckets land — but the gate lives here, at the merge point,
        // because that is where the merged edge list first exists.
        foreach (var edge in edges)
        {
            var prereqUnknown = !itemMap.ContainsKey(edge.PrerequisiteItemId);
            var depUnknown = !itemMap.ContainsKey(edge.DependentItemId);
            if (!prereqUnknown && !depUnknown) continue;

            var unknownId = prereqUnknown ? edge.PrerequisiteItemId : edge.DependentItemId;
            conflicts.Add(new EdgeConflict(
                EdgeConflictKind.UnknownItem,
                new[] { edge },
                $"Edge references unknown item id {unknownId}"));
        }

        // Pass 2: cycles. Restrict to edges with both endpoints known —
        // unknown-endpoint edges are already flagged above and would
        // otherwise add noise to the cycle adjacency.
        var knownEdges = new List<CrossItemEdge>(edges.Count);
        foreach (var edge in edges)
        {
            if (itemMap.ContainsKey(edge.PrerequisiteItemId) && itemMap.ContainsKey(edge.DependentItemId))
            {
                knownEdges.Add(edge);
            }
        }
        conflicts.AddRange(DetectCycles(knownEdges));

        return conflicts;
    }

    /// <summary>
    /// DFS-with-coloring cycle detection. Each detected back edge yields
    /// one <see cref="EdgeConflict"/> with the cycle's edges in traversal
    /// order, starting from the back-edge target.
    /// </summary>
    /// <remarks>
    /// Nodes in the cycle graph are (item id, requirement kind) tuples,
    /// not bare item ids. PR #1's definitional output has bidirectional
    /// flow at the item granularity (children-unblock parent→child;
    /// terminal-rollup child→parent), but the two edges target distinct
    /// requirement kinds — so at the requirement-kind granularity the
    /// graph is acyclic. The conflict description uses item ids only,
    /// matching the human-readable form requested by the conflict gate.
    /// </remarks>
    private static IReadOnlyList<EdgeConflict> DetectCycles(
        IReadOnlyList<CrossItemEdge> edges)
    {
        // Node = (item id, requirement kind). Build the node set from
        // both endpoints of every edge.
        var nodes = new HashSet<(int Id, string Kind)>();
        foreach (var edge in edges)
        {
            nodes.Add((edge.PrerequisiteItemId, edge.PrerequisiteKind));
            nodes.Add((edge.DependentItemId, edge.DependentKind));
        }

        // Adjacency: node -> outgoing edges. Sort outgoing edges
        // deterministically so cycle traversal order is stable.
        var adjacency = new Dictionary<(int Id, string Kind), List<CrossItemEdge>>(nodes.Count);
        foreach (var node in nodes)
        {
            adjacency[node] = new List<CrossItemEdge>();
        }
        foreach (var edge in edges)
        {
            adjacency[(edge.PrerequisiteItemId, edge.PrerequisiteKind)].Add(edge);
        }
        foreach (var outgoing in adjacency.Values)
        {
            outgoing.Sort(static (a, b) =>
            {
                var c = a.DependentItemId.CompareTo(b.DependentItemId);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.DependentKind, b.DependentKind);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.PrerequisiteKind, b.PrerequisiteKind);
                if (c != 0) return c;
                return string.CompareOrdinal(a.RequiredDisposition, b.RequiredDisposition);
            });
        }

        // 0 = white (unvisited), 1 = gray (on current DFS stack), 2 = black (done).
        var color = new Dictionary<(int Id, string Kind), byte>(adjacency.Count);
        foreach (var node in adjacency.Keys) color[node] = 0;

        var conflicts = new List<EdgeConflict>();
        var path = new List<(int Id, string Kind)>();
        var pathEdgeTo = new Dictionary<(int Id, string Kind), CrossItemEdge>();

        void Dfs((int Id, string Kind) u)
        {
            color[u] = 1;
            path.Add(u);

            foreach (var edge in adjacency[u])
            {
                var v = (edge.DependentItemId, edge.DependentKind);
                if (color[v] == 1)
                {
                    // Back edge u → v. Slice the cycle out of the current path.
                    var idx = path.IndexOf(v);
                    var cycleEdges = new List<CrossItemEdge>();
                    var cycleIds = new List<int> { v.DependentItemId };
                    for (var i = idx + 1; i < path.Count; i++)
                    {
                        cycleEdges.Add(pathEdgeTo[path[i]]);
                        cycleIds.Add(path[i].Id);
                    }
                    cycleEdges.Add(edge);
                    cycleIds.Add(v.DependentItemId);

                    var description = "Cycle detected: " + string.Join(" -> ", cycleIds);
                    conflicts.Add(new EdgeConflict(EdgeConflictKind.Cycle, cycleEdges, description));
                }
                else if (color[v] == 0)
                {
                    pathEdgeTo[v] = edge;
                    Dfs(v);
                }
            }

            color[u] = 2;
            path.RemoveAt(path.Count - 1);
            pathEdgeTo.Remove(u);
        }

        // Start DFS from each unvisited node in (id, kind) ascending order
        // so that cycle ordering is deterministic and matches "smallest
        // item id first" for disjoint cycles.
        var sortedNodes = adjacency.Keys.ToList();
        sortedNodes.Sort(static (a, b) =>
        {
            var c = a.Id.CompareTo(b.Id);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Kind, b.Kind);
        });
        foreach (var node in sortedNodes)
        {
            if (color[node] == 0) Dfs(node);
        }

        return conflicts;
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
