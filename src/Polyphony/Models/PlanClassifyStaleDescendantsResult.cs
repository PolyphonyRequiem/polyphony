namespace Polyphony;

/// <summary>
/// Result emitted by <c>polyphony plan classify-stale-descendants</c>.
/// Walks the full descendant tree of <paramref name="rootId"/>, finds
/// open plan PRs, parses their <c>ancestor_plan_generations</c> snapshot,
/// and reports each whose snapshot is behind the current manifest.
///
/// <para>P9 (ancestor cascade) consumes this list to drive the per-PR
/// remedy step (auto-rebase / human-gate / recreate). This verb is
/// strictly classification — it does not modify any branches or PRs.</para>
///
/// <para>Always exits 0 (routing-style verb). Workflow branches on the
/// presence of <see cref="Error"/>.</para>
/// </summary>
public sealed record PlanClassifyStaleDescendantsResult
{
    /// <summary>The root work item id whose descendant tree was walked.</summary>
    public required int RootId { get; init; }

    /// <summary>Git ref the manifest was read from (e.g. <c>origin/feature/100</c>).</summary>
    public string ManifestRef { get; init; } = string.Empty;

    /// <summary>Path inside <see cref="ManifestRef"/> the manifest was read from.</summary>
    public string ManifestPath { get; init; } = string.Empty;

    /// <summary>
    /// Total descendants visited (excludes the root itself). Useful for
    /// telemetry / "did the walk cover the whole tree?" verification.
    /// </summary>
    public int TotalDescendantsScanned { get; init; }

    /// <summary>
    /// Subset of descendants that had at least one open plan PR matching
    /// their <c>plan/{root}-{item}</c> branch.
    /// </summary>
    public int TotalDescendantsWithOpenPrs { get; init; }

    /// <summary>Count of entries in <see cref="StaleDescendants"/> — convenience for routing.</summary>
    public int TotalStale { get; init; }

    /// <summary>
    /// One entry per descendant whose open plan PR's snapshot is behind
    /// the current manifest's <see cref="Polyphony.Manifest.RunManifest.PlanGenerations"/>
    /// for at least one ancestor key.
    /// </summary>
    public IReadOnlyList<StalePlanPrDescendant> StaleDescendants { get; init; } = [];

    /// <summary>
    /// Non-blocking warnings — e.g., a descendant's PR poll failed and
    /// was skipped, manifest had no entry for an ancestor key, etc.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Error message on a verb-level failure; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Categorical error code on failure; null on success.</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// One descendant whose open plan PR's snapshot is behind the manifest
/// for at least one ancestor.
/// </summary>
public sealed record StalePlanPrDescendant
{
    /// <summary>The descendant work item id.</summary>
    public required int ItemId { get; init; }

    /// <summary>PR number for the descendant's open plan PR.</summary>
    public required int PrNumber { get; init; }

    /// <summary>PR URL for the descendant's open plan PR.</summary>
    public string PrUrl { get; init; } = string.Empty;

    /// <summary>PR head ref name (e.g. <c>plan/100-300</c>).</summary>
    public string HeadRef { get; init; } = string.Empty;

    /// <summary>Head commit SHA captured at the time of classification.</summary>
    public string? HeadSha { get; init; }

    /// <summary>
    /// Per-ancestor staleness entries — one row per ancestor whose
    /// snapshot generation is behind the manifest. Reuses the type
    /// defined in <c>PrMergePlanPrResult.cs</c> so the consumer can
    /// pass these straight through to the same diff renderer used by
    /// the merge-time stale-generation block.
    /// </summary>
    public IReadOnlyList<StaleAncestorEntry> StaleAncestors { get; init; } = [];
}
