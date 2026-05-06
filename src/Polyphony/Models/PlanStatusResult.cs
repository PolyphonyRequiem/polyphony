namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan status</c> — operator-facing snapshot of the
/// plan-PR ledger across the run hierarchy. Reads the <c>merged_plan_prs</c>
/// ledger and <c>plan_generations</c> map from <c>.polyphony/run.yaml</c> and
/// groups ledger entries by <see cref="PlanStatusItem.ItemId"/>.
///
/// <para>Routing-style verb: always exits 0; consumers branch on the
/// <see cref="Error"/> field. The verb is read-only — it never mutates the
/// manifest or hits the platform.</para>
/// </summary>
public sealed record PlanStatusResult
{
    /// <summary>Run-root work-item id supplied via <c>--root-id</c> (echoed for traceability).</summary>
    public int RootId { get; init; }

    /// <summary>Absolute path the manifest was loaded from. Empty when discovery failed.</summary>
    public string ManifestPath { get; init; } = "";

    /// <summary>
    /// One entry per item that has at least one merged plan PR in the ledger,
    /// sorted by <see cref="PlanStatusItem.ItemId"/> ascending. Empty when the
    /// ledger contains no entries.
    /// </summary>
    public IReadOnlyList<PlanStatusItem> Items { get; init; } = Array.Empty<PlanStatusItem>();

    /// <summary>
    /// Operator-facing error message when the snapshot cannot be produced
    /// (missing manifest, root-id mismatch, malformed YAML, etc.). Null on success.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// Per-item summary row in <see cref="PlanStatusResult.Items"/>. Aggregates
/// the ledger entries for one work item into the most operator-relevant
/// signals: current generation, total merged-PR count, and the most recent
/// merged PR (by <see cref="LatestMergedAt"/>).
/// </summary>
public sealed record PlanStatusItem
{
    /// <summary>
    /// Work-item id this row aggregates. Either the run root (when the
    /// ledger entry's <c>item_key</c> is the literal <c>"root"</c>) or a
    /// descendant id parsed from a numeric <c>item_key</c>.
    /// </summary>
    public int ItemId { get; init; }

    /// <summary>
    /// Current generation as recorded in
    /// <see cref="Polyphony.Manifest.RunManifest.PlanGenerations"/> for this
    /// item's key. 0 when the manifest map has no entry (e.g. ledger entry
    /// orphaned from the generations map).
    /// </summary>
    public int CurrentGeneration { get; init; }

    /// <summary>Total merged-PR count for this item across the whole ledger.</summary>
    public int MergedPrCount { get; init; }

    /// <summary>
    /// URL of the most recently merged PR for this item, derived from the
    /// platform_project + ledger PR number. Null when the ledger entry has
    /// no usable platform-project value.
    /// </summary>
    public string? LatestPrUrl { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent ledger append for this item
    /// (<see cref="Polyphony.Manifest.MergedPlanPrEntry.RecordedAt"/>). Null
    /// when no entries exist (cannot occur in emitted output but kept
    /// nullable for clarity).
    /// </summary>
    public DateTime? LatestMergedAt { get; init; }
}
