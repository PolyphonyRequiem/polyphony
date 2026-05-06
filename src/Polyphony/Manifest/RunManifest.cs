namespace Polyphony.Manifest;

/// <summary>
/// The on-disk run manifest at <c>.polyphony/run.yaml</c>. Schema 1 per
/// the Rev 4 branch-model ADR (<c>docs/decisions/branch-model.md</c> §
/// Run manifest). DTO uses primitive types; domain-typed conversions
/// happen at the validator / consumer boundary.
///
/// Field grouping (normative shape):
/// <list type="bullet">
///   <item><description>Identity: <see cref="Schema"/>, <see cref="RootId"/>, <see cref="PlatformProject"/>, <see cref="CreatedAt"/>, <see cref="CreatedBy"/>, <see cref="BranchModelVersion"/>.</description></item>
///   <item><description>Topology (hashed): <see cref="MergeGroups"/>. <see cref="TopologyHash"/> is the SHA-256 over the canonicalized form.</description></item>
///   <item><description>Plan generations (cross-cutting bookkeeping): <see cref="PlanGenerations"/>.</description></item>
///   <item><description>Operational/audit (NOT hashed): <see cref="Rebases"/>, <see cref="HumanApprovals"/>, <see cref="RetiredMergeGroupIds"/>.</description></item>
/// </list>
/// </summary>
public sealed class RunManifest
{
    /// <summary>Manifest schema version. Always 1 for current builds.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>The run's apex (focus) work-item id.</summary>
    public int RootId { get; set; }

    /// <summary>
    /// Platform-qualified project identifier (e.g.
    /// <c>dev.azure.com/dangreen-msft/Twig</c>) used to disambiguate
    /// collisions across orgs/instances.
    /// </summary>
    public string PlatformProject { get; set; } = string.Empty;

    /// <summary>Manifest creation timestamp (UTC, ISO-8601 with <c>Z</c>).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Operator / actor that initiated the run.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Branch-model schema version (ADR Rev 4 = 1).</summary>
    public int BranchModelVersion { get; set; } = 1;

    /// <summary>
    /// Per-item plan-generation counters. Key is either the literal
    /// string <c>"root"</c> or a numeric child id (as a string). Values
    /// monotonically increase as plans are revised.
    /// </summary>
    public Dictionary<string, int> PlanGenerations { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// SHA-256 over the canonicalized merge-group records (see
    /// <see cref="TopologyHasher"/>). Stored as <c>sha256:{hex}</c>.
    /// Recomputed on every save by <see cref="RunManifestStore.Save"/>.
    /// </summary>
    public string TopologyHash { get; set; } = string.Empty;

    /// <summary>The merge groups for this run, in declaration order.</summary>
    public List<MergeGroupEntry> MergeGroups { get; set; } = new();

    /// <summary>
    /// Recorded rebase events for auditability. Append-only by convention
    /// (the manifest store does not enforce ordering).
    /// </summary>
    public List<RebaseRecord> Rebases { get; set; } = new();

    /// <summary>Recorded human-gate approvals (deep nesting, retirement, force overrides).</summary>
    public List<HumanApprovalRecord> HumanApprovals { get; set; } = new();

    /// <summary>Retired merge-group ids — cannot be reused under this root.</summary>
    public List<RetiredMergeGroupRecord> RetiredMergeGroupIds { get; set; } = new();
}

/// <summary>
/// A single merge-group entry in <see cref="RunManifest.MergeGroups"/>.
/// </summary>
public sealed class MergeGroupEntry
{
    /// <summary>
    /// The terminal segment (e.g. <c>data-layer</c>, <c>item-4567</c>).
    /// Must satisfy the MG-id grammar. <c>"flat"</c> is reserved and
    /// MUST NOT be used as an id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The canonical <c>_</c>-joined chain from the root (top-level =>
    /// single segment matching <see cref="Id"/>). Participates in the
    /// topology hash verbatim.
    /// </summary>
    public string MgPath { get; set; } = string.Empty;

    /// <summary>
    /// The parent's <see cref="MgPath"/>, or <c>null</c> for top-level
    /// merge groups.
    /// </summary>
    public string? ParentMgPath { get; set; }

    /// <summary>The work-item ids owned by this merge group.</summary>
    public List<int> Items { get; set; } = new();

    /// <summary>
    /// <c>"top"</c> or <c>"nested"</c> — see
    /// <see cref="ManifestNesting"/>. MUST be consistent with
    /// <see cref="ParentMgPath"/> being null vs non-null.
    /// </summary>
    public string Nesting { get; set; } = ManifestNesting.Top;

    /// <summary>
    /// <c>"per-merge-group"</c> or <c>"per-item"</c> — see
    /// <see cref="ManifestIsolation"/>. Hyphenated wire form is
    /// normative; participates in the topology hash verbatim.
    /// </summary>
    public string Isolation { get; set; } = ManifestIsolation.PerMergeGroup;

    /// <summary>
    /// The planner override applied to this entry: <c>null</c> (no
    /// override), <c>"flat"</c> (the reserved sentinel), or a valid
    /// MG-id string. Participates in the topology hash; canonicalized
    /// to the literal string <c>"null"</c> when absent.
    /// </summary>
    public string? NestingOverride { get; set; }
}

/// <summary>
/// One recorded rebase event. Reasons follow the ADR's enumerated set:
/// <c>cross_mg_code_dep</c>, <c>child_plan_drift</c>, <c>manual</c>.
/// </summary>
public sealed class RebaseRecord
{
    /// <summary>The branch that was rebased (e.g. <c>mg/1234_data-layer</c>).</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>The branch the rebase landed onto (e.g. <c>feature/1234</c>).</summary>
    public string Onto { get; set; } = string.Empty;

    /// <summary>Categorical reason for the rebase.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The new HEAD commit after the rebase.</summary>
    public string Commit { get; set; } = string.Empty;

    /// <summary>When the rebase was recorded (UTC).</summary>
    public DateTime RecordedAt { get; set; }
}

/// <summary>
/// One recorded human-gate approval (deep nesting, retirement,
/// force-overrides, etc.).
/// </summary>
public sealed class HumanApprovalRecord
{
    /// <summary>The named gate that was approved (e.g. <c>deep_nesting_depth_4</c>).</summary>
    public string Gate { get; set; } = string.Empty;

    /// <summary>The approver's display name / handle.</summary>
    public string ApprovedBy { get; set; } = string.Empty;

    /// <summary>When the approval was recorded (UTC).</summary>
    public DateTime ApprovedAt { get; set; }

    /// <summary>Free-form detail describing what was approved.</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// One retired MG id. Once retired under a given root, the id MUST NOT
/// be reused for the lifetime of the run.
/// </summary>
public sealed class RetiredMergeGroupRecord
{
    /// <summary>The retired MG id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>When the retirement was recorded (UTC).</summary>
    public DateTime RetiredAt { get; set; }

    /// <summary>Free-form reason explaining why the MG was retired.</summary>
    public string? Reason { get; set; }
}
