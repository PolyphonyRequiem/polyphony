namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worklist build</c> — the ordered list of plan-tree
/// work items, grouped into "waves" that can be dispatched in parallel by
/// the (future) apex driver workflow. A wave is ready when all items in
/// earlier waves have reached a terminal state (e.g. plan PR merged, or
/// skipped).
///
/// <para>Routing-style verb: always exits 0; consumers branch on the
/// <see cref="HasConflicts"/> + <see cref="Conflicts"/> pair, then on the
/// <see cref="Error"/> / <see cref="ErrorCode"/> fields. The verb is
/// pure-read — it never mutates the manifest or hits the platform.</para>
///
/// <para>Wave ordering is computed by <see cref="Polyphony.Sdlc.EdgeGraph.ToWaves"/>
/// over the merged within-item + cross-item edge graph (Phase 7 PR #7
/// hard cutover from BFS-by-depth). Wave 0 contains items whose entry
/// requirements have no inbound cross-item edges — typically the run
/// root, plus items whose parent is a non-plannable container. When
/// <see cref="HasConflicts"/> is true, <see cref="Waves"/> is the empty
/// array — consumers route to a conflict-resolution gate before
/// inspecting waves.</para>
/// </summary>
public sealed record WorklistResult
{
    /// <summary>Run-root work-item id supplied via <c>--root-id</c> (echoed for traceability).</summary>
    public required int RootId { get; init; }

    /// <summary>
    /// Number of items the verb reached during the BFS walk. Zero on
    /// errors that fail before the walk completes (missing manifest,
    /// missing root, etc.). Always populated.
    /// </summary>
    public required int ItemsWalked { get; init; }

    /// <summary>
    /// Convenience flag — equivalent to <c>Conflicts.Count &gt; 0</c>.
    /// Workflow YAML routes on this rather than re-counting client-side.
    /// Always <c>false</c> on error envelopes (errors live in
    /// <see cref="Error"/> / <see cref="ErrorCode"/>, not in
    /// <see cref="Conflicts"/>).
    /// </summary>
    public required bool HasConflicts { get; init; }

    /// <summary>
    /// Conflicts detected during edge graph build, in the deterministic
    /// order emitted by <see cref="Polyphony.Sdlc.EdgeGraph.Build"/>
    /// (unknown-item entries first, then cycles smallest-id-first).
    /// Always present (empty array on a clean build) so workflow
    /// consumers can read this field without first inspecting
    /// <see cref="HasConflicts"/>. Shape matches the
    /// <c>edges check</c> verb's <c>conflicts[]</c> array (same record
    /// type) so consumers can share rendering code.
    /// </summary>
    public required IReadOnlyList<EdgesCheckConflict> Conflicts { get; init; }

    /// <summary>
    /// Ordered waves (wave 0 = items with no inbound cross-item edges
    /// into their entry requirements). Each wave's items can be
    /// dispatched in parallel; wave N depends on wave N-1 having reached
    /// a terminal state. Empty when the verb errored before walking
    /// the tree, or when <see cref="HasConflicts"/> is true (consumers
    /// must resolve conflicts before consuming waves).
    /// </summary>
    public required IReadOnlyList<WorklistWave> Waves { get; init; }

    /// <summary>Operator-facing error message when the worklist cannot be produced. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>manifest_not_found</c>,
    /// <c>manifest_invalid</c>, <c>root_id_mismatch</c>,
    /// <c>root_not_found</c>, <c>type_unknown</c>,
    /// <c>derivation_failed</c>, <c>cache_error</c>,
    /// <c>graph_invalid</c>. Null on success.
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// One wave in <see cref="WorklistResult.Waves"/> — a set of work items
/// whose entry requirements all become ready at the same topological
/// depth (per <see cref="Polyphony.Sdlc.EdgeGraph.ToWaves"/>).
/// </summary>
public sealed record WorklistWave(int WaveIndex, IReadOnlyList<WorklistItem> Items);

/// <summary>
/// One work item entry in <see cref="WorklistWave.Items"/>. Carries the
/// item's plan-PR status and current generation so the dispatcher can
/// decide whether to act on it without consulting the manifest itself.
/// </summary>
/// <param name="ItemId">The work item id.</param>
/// <param name="ParentItemId">The plan-tree parent id; <c>0</c> for the root.</param>
/// <param name="PlanStatus">
/// One of <c>pending</c> | <c>open</c> | <c>merged</c> | <c>unknown</c>.
/// <list type="bullet">
///   <item><c>merged</c> — the item has at least one merged plan-PR ledger entry.</item>
///   <item><c>open</c> — reserved for future use when open-PR tracking lands.</item>
///   <item><c>pending</c> — no plan PR yet (default for items reachable via the tree walk).</item>
///   <item><c>unknown</c> — the work item could not be located in the local twig cache.</item>
/// </list>
/// </param>
/// <param name="PlanPrNumber">
/// Most recent merged plan-PR number for this item, or null when the
/// item has no merged plan PR yet.
/// </param>
/// <param name="CurrentGeneration">
/// Current generation as recorded in
/// <see cref="Polyphony.Manifest.RunManifest.PlanGenerations"/> for this
/// item's key. <c>0</c> when the manifest map has no entry.
/// </param>
public sealed record WorklistItem(
    int ItemId,
    int ParentItemId,
    string PlanStatus,
    int? PlanPrNumber,
    int CurrentGeneration);
