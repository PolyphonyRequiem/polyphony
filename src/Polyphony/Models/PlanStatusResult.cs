namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan status</c> — operator-facing snapshot of the
/// plan-PR state for every plannable item in the run hierarchy. Walks the
/// twig cache from <c>--root</c>, derives each item's facets from the
/// <see cref="Polyphony.Configuration.ProcessConfig"/>, and queries
/// <c>gh</c> for the open / merged / abandoned PR shape per plan branch.
///
/// <para>Plan generation is enriched from the run manifest's
/// <see cref="Polyphony.Manifest.RunManifest.PlanGenerations"/> map when
/// the manifest is available; manifest absence is non-fatal — the verb
/// still walks and reports on the tree, leaving generation null.</para>
///
/// <para>Routing-style verb: always exits 0; consumers branch on
/// <see cref="ErrorCode"/>. The verb is read-only — no manifest mutation,
/// no platform-side mutation.</para>
/// </summary>
public sealed record PlanStatusResult
{
    /// <summary>True when the verb produced a populated snapshot. False when an
    /// <see cref="ErrorCode"/> was raised; in that case <see cref="Items"/> is empty
    /// and <see cref="Summary"/> contains zeroed counters.</summary>
    public bool Success { get; init; }

    /// <summary>Run-root work-item id supplied via <c>--root</c> (echoed for traceability).</summary>
    public int RootId { get; init; }

    /// <summary>
    /// One entry per item walked from <see cref="RootId"/>, sorted by
    /// <see cref="PlanStatusItem.ItemId"/> ascending. When <c>--include-na</c>
    /// is false (default), items with <see cref="PlanStatusItem.PlanStatus"/>
    /// == <c>"n/a"</c> are omitted from the array; <see cref="Summary"/>
    /// counters always include them so the operator can see the full size of
    /// the tree.
    /// </summary>
    public IReadOnlyList<PlanStatusItem> Items { get; init; } = Array.Empty<PlanStatusItem>();

    /// <summary>Aggregate counters across the entire walked tree (including
    /// <c>n/a</c> items that <c>--include-na=false</c> hides from
    /// <see cref="Items"/>).</summary>
    public PlanStatusSummary Summary { get; init; } = PlanStatusSummary.Empty;

    /// <summary>Stable, machine-routable error code. Null on success.
    /// Documented values:
    /// <list type="bullet">
    ///   <item><c>invalid_argument</c> — <c>--root</c> non-positive.</item>
    ///   <item><c>root_not_found</c> — <c>--root</c> does not resolve in the twig cache.</item>
    ///   <item><c>type_unknown</c> — a walked item's type is not registered in the process config.</item>
    ///   <item><c>manifest_not_found</c> — <c>--manifest</c> path is missing (when explicitly supplied).</item>
    ///   <item><c>manifest_invalid</c> — manifest YAML failed to parse / validate.</item>
    ///   <item><c>root_id_mismatch</c> — manifest's root_id does not match <c>--root</c>.</item>
    ///   <item><c>no_repo_slug</c> — could not resolve a github.com slug from <c>git remote get-url origin</c> and no <c>--repo</c> was supplied.</item>
    ///   <item><c>gh_failed</c> — gh subprocess failed (timeout, auth, network) while enumerating PRs for at least one item.</item>
    /// </list>
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error message. Null on success.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Per-item plan-PR snapshot row in <see cref="PlanStatusResult.Items"/>.
/// </summary>
public sealed record PlanStatusItem
{
    /// <summary>Work-item id this row reports on.</summary>
    public int ItemId { get; init; }

    /// <summary>Twig title of the item (best-effort — empty when twig has no title).</summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// Plan-PR lifecycle state for this item. One of:
    /// <list type="bullet">
    ///   <item><c>"needed"</c> — item has the plannable facet but no plan PR exists yet.</item>
    ///   <item><c>"open"</c> — plan PR exists and is OPEN on the platform.</item>
    ///   <item><c>"merged"</c> — plan PR has been merged.</item>
    ///   <item><c>"abandoned"</c> — plan PR was closed without merging.</item>
    ///   <item><c>"n/a"</c> — item has no plannable facet (skipped from the items array unless <c>--include-na</c> is set).</item>
    /// </list>
    /// </summary>
    public string PlanStatus { get; init; } = "n/a";

    /// <summary>The plan PR's number (when one exists). Null for <c>needed</c> and <c>n/a</c>.</summary>
    public int? PlanPrNumber { get; init; }

    /// <summary>The plan PR's URL on the platform (when one exists). Null for <c>needed</c> and <c>n/a</c>.</summary>
    public string? PlanPrUrl { get; init; }

    /// <summary>
    /// Current plan generation as recorded in the manifest's
    /// <see cref="Polyphony.Manifest.RunManifest.PlanGenerations"/> map. Null
    /// for <c>needed</c> (planning has not started) and <c>n/a</c>; null also
    /// when the manifest is absent or the map has no entry for this item.
    /// </summary>
    public int? PlanGeneration { get; init; }

    /// <summary>
    /// True when the open plan PR carries an unresolved
    /// <c>CHANGES_REQUESTED</c> review decision — the reviewer asked for
    /// changes that the next plan-PR push has not yet addressed. Only
    /// populated when <see cref="PlanStatus"/> is <c>"open"</c> (null
    /// otherwise) so consumers can distinguish "no signal" (n/a, needed,
    /// merged, abandoned) from "open, no pending revisions" (false) from
    /// "open, changes requested" (true).
    /// </summary>
    public bool? PendingRevisions { get; init; }
}

/// <summary>
/// Aggregate counters across all items walked from <see cref="PlanStatusResult.RootId"/>.
/// Counters always reflect the full tree, even when <c>--include-na=false</c>
/// hides <c>n/a</c> rows from <see cref="PlanStatusResult.Items"/> — the
/// summary is the operator's "is anything missing" signal and must not
/// understate scope.
/// </summary>
public sealed record PlanStatusSummary
{
    /// <summary>The empty / zeroed summary used on error paths.</summary>
    public static readonly PlanStatusSummary Empty = new();

    /// <summary>Total items walked from <c>--root</c> (inclusive).</summary>
    public int TotalItems { get; init; }

    /// <summary>Items with <see cref="PlanStatusItem.PlanStatus"/> == <c>"needed"</c>.</summary>
    public int PlanNeeded { get; init; }

    /// <summary>Items with <see cref="PlanStatusItem.PlanStatus"/> == <c>"open"</c>.</summary>
    public int PlanOpen { get; init; }

    /// <summary>Items with <see cref="PlanStatusItem.PlanStatus"/> == <c>"merged"</c>.</summary>
    public int PlanMerged { get; init; }

    /// <summary>Items with <see cref="PlanStatusItem.PlanStatus"/> == <c>"abandoned"</c>.</summary>
    public int PlanAbandoned { get; init; }

    /// <summary>Items with <see cref="PlanStatusItem.PlanStatus"/> == <c>"n/a"</c>.
    /// Serialized as <c>plan_n_a</c> per the snake_case naming convention —
    /// the <c>_n_a_</c> spelling preserves the slash-as-word-boundary the
    /// enum value carries; consumers that grep for <c>plan_</c>-prefixed
    /// counters then read each as a single token without re-escaping.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("plan_n_a")]
    public int PlanNa { get; init; }

    /// <summary>Subset of <see cref="PlanOpen"/> where
    /// <see cref="PlanStatusItem.PendingRevisions"/> is <c>true</c> — operator's
    /// shortcut for "how many open plan PRs need attention right now."</summary>
    public int PendingRevisions { get; init; }
}
