using Polyphony.Manifest;

namespace Polyphony;

/// <summary>JSON output for <c>polyphony manifest read</c>.</summary>
public sealed record ManifestReadResult
{
    /// <summary>Path the manifest was read from.</summary>
    public required string Path { get; init; }

    /// <summary>The full manifest contents (snake-case via JsonContext).</summary>
    public required RunManifest Manifest { get; init; }

    /// <summary>The freshly-computed topology hash for the loaded merge groups.</summary>
    public required string ComputedTopologyHash { get; init; }

    /// <summary>True iff the stored hash matches the computed hash.</summary>
    public required bool TopologyHashMatches { get; init; }

    /// <summary>Validation error when read fails.</summary>
    public string? Error { get; init; }
}

/// <summary>JSON output for <c>polyphony manifest topology-hash</c>.</summary>
public sealed record ManifestTopologyHashResult
{
    /// <summary>Path the manifest was read from.</summary>
    public required string Path { get; init; }

    /// <summary>The freshly-computed topology hash.</summary>
    public required string TopologyHash { get; init; }

    /// <summary>The hash currently stored in the manifest file.</summary>
    public required string StoredTopologyHash { get; init; }

    /// <summary>True iff the stored hash matches the freshly-computed hash.</summary>
    public required bool Matches { get; init; }

    /// <summary>Validation error when computation fails.</summary>
    public string? Error { get; init; }
}

/// <summary>JSON output for <c>polyphony manifest record-rebase</c>.</summary>
public sealed record ManifestRebaseRecordResult
{
    /// <summary>Path the manifest was modified at.</summary>
    public required string Path { get; init; }

    /// <summary>The total number of recorded rebases after this append.</summary>
    public required int RebaseCount { get; init; }

    /// <summary>The branch the rebase targeted.</summary>
    public required string Branch { get; init; }

    /// <summary>The branch the rebase landed onto.</summary>
    public required string Onto { get; init; }

    /// <summary>The recorded reason category.</summary>
    public required string Reason { get; init; }

    /// <summary>The post-rebase HEAD commit.</summary>
    public required string Commit { get; init; }

    /// <summary>UTC timestamp recorded with the rebase.</summary>
    public required DateTime RecordedAt { get; init; }

    /// <summary>Validation error when the operation fails.</summary>
    public string? Error { get; init; }
}

/// <summary>JSON output for <c>polyphony manifest record-approval</c>.</summary>
public sealed record ManifestApprovalRecordResult
{
    /// <summary>Path the manifest was modified at.</summary>
    public required string Path { get; init; }

    /// <summary>The total number of recorded approvals after this append.</summary>
    public required int ApprovalCount { get; init; }

    /// <summary>The named gate that was approved.</summary>
    public required string Gate { get; init; }

    /// <summary>The approver's display name.</summary>
    public required string ApprovedBy { get; init; }

    /// <summary>UTC timestamp recorded with the approval.</summary>
    public required DateTime ApprovedAt { get; init; }

    /// <summary>Free-form detail.</summary>
    public string? Detail { get; init; }

    /// <summary>Validation error when the operation fails.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// JSON output for <c>polyphony manifest record-plan-merge</c>. Bumps the
/// generation counter for the named plan (root or descendant) and reports
/// the previous and current values.
///
/// <para>When the call carries a <c>--pr-number</c> and the verb finds a
/// matching ledger entry, the bump is skipped (idempotent re-entry) and
/// <see cref="Recorded"/> is <c>false</c>; the reported
/// <see cref="PreviousGeneration"/> and <see cref="CurrentGeneration"/>
/// then reflect the values that were written by the original recording.</para>
/// </summary>
public sealed record ManifestRecordPlanMergeResult
{
    /// <summary>Path the manifest was modified at.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The plan key whose generation was bumped. Either the literal
    /// <c>"root"</c> (root plan) or the work-item id as a string
    /// (descendant plan). Matches the key used in
    /// <see cref="RunManifest.PlanGenerations"/>.
    /// </summary>
    public required string ItemKey { get; init; }

    /// <summary>Generation before the bump (0 if the entry was missing).</summary>
    public required int PreviousGeneration { get; init; }

    /// <summary>Generation after the bump (always <c>previous + 1</c> on a fresh record).</summary>
    public required int CurrentGeneration { get; init; }

    /// <summary>
    /// True when this call wrote a new ledger entry and bumped the
    /// generation. False when the verb detected that the supplied
    /// <c>--pr-number</c> was already recorded with matching identity
    /// and skipped the bump (idempotent re-entry).
    /// </summary>
    public required bool Recorded { get; init; }

    /// <summary>
    /// The PR number associated with this record, or <c>0</c> when the
    /// call omitted <c>--pr-number</c> (legacy callers; no ledger entry
    /// is appended).
    /// </summary>
    public required int PrNumber { get; init; }

    /// <summary>
    /// The merge commit SHA associated with this record, or empty when
    /// the call omitted <c>--merge-commit</c>.
    /// </summary>
    public required string MergeCommit { get; init; }

    /// <summary>Validation error when the operation fails.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// JSON output for <c>polyphony manifest read-plan-generation</c>. Reports
/// the current generation for a single plan key (returns 0 when the entry
/// is missing — generations start at 0 by convention).
/// </summary>
public sealed record ManifestReadPlanGenerationResult
{
    /// <summary>Path the manifest was read from.</summary>
    public required string Path { get; init; }

    /// <summary>The plan key looked up.</summary>
    public required string ItemKey { get; init; }

    /// <summary>
    /// Current generation for the plan, or 0 when no entry exists in the
    /// manifest yet (i.e. no plan has been merged for this key).
    /// </summary>
    public required int Generation { get; init; }

    /// <summary>True when the manifest had an explicit entry; false when the read returned the default (0).</summary>
    public required bool Present { get; init; }

    /// <summary>Validation error when the operation fails.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// JSON output for <c>polyphony manifest read-plan-generation-snapshot</c>.
/// Returns the immediate-parent generation plus a map of all named ancestor
/// generations, suitable for embedding in a plan PR's body front-matter so
/// staleness can later be detected.
/// </summary>
public sealed record ManifestReadPlanGenerationSnapshotResult
{
    /// <summary>Path the manifest was read from.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The plan key the snapshot is being computed for. Literal
    /// <c>"root"</c> for the root plan or numeric id for descendants.
    /// </summary>
    public required string ItemKey { get; init; }

    /// <summary>
    /// Immediate-parent plan key, or null when this is the root plan. For a
    /// direct child of the root plan, this is <c>"root"</c>.
    /// </summary>
    public string? ParentItemKey { get; init; }

    /// <summary>
    /// Generation of the immediate parent at snapshot time, or 0 when this
    /// is the root plan or the parent has no recorded generation yet.
    /// </summary>
    public required int ParentPlanGeneration { get; init; }

    /// <summary>
    /// Map from ancestor key to ancestor generation, covering the entire
    /// declared ancestor chain. Keys not present in the manifest are
    /// included as 0 so the snapshot is complete and unambiguous. Empty
    /// map for the root plan.
    /// </summary>
    public required Dictionary<string, int> AncestorPlanGenerations { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Validation error when the operation fails.</summary>
    public string? Error { get; init; }
}
