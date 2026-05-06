namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worklist build</c> — the ordered list of plan-tree
/// work items, grouped into "waves" that can be dispatched in parallel by
/// the (future) tree-walker workflow. A wave is ready when all items in
/// earlier waves have reached a terminal state (e.g. plan PR merged, or
/// skipped).
///
/// <para>Routing-style verb: always exits 0; consumers branch on the
/// <see cref="Error"/> / <see cref="ErrorCode"/> fields. The verb is
/// pure-read — it never mutates the manifest or hits the platform.</para>
/// </summary>
public sealed record WorklistResult
{
    /// <summary>Run-root work-item id supplied via <c>--root-id</c> (echoed for traceability).</summary>
    public required int RootId { get; init; }

    /// <summary>
    /// Ordered waves (wave 0 = root only). Each wave's items can be
    /// dispatched in parallel; wave N depends on wave N-1 having reached
    /// a terminal state. Empty when the verb errored before walking
    /// the tree.
    /// </summary>
    public required IReadOnlyList<WorklistWave> Waves { get; init; }

    /// <summary>Operator-facing error message when the worklist cannot be produced. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>manifest_not_found</c>,
    /// <c>manifest_invalid</c>, <c>root_id_mismatch</c>,
    /// <c>root_not_found</c>. Null on success.
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// One wave in <see cref="WorklistResult.Waves"/> — a set of work items
/// at the same plan-tree depth that can be dispatched concurrently.
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
