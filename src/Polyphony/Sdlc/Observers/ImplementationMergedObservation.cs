namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Observation of the <c>implementation_merged</c> requirement kind for a
/// single work item. Produced by
/// <see cref="PlanObserver.ObserveImplementationMergedAsync"/> from the
/// live state of the latest PR for the canonical impl branch
/// (<c>impl/{root}-{item}</c> per <c>docs/decisions/branch-model.md</c> /
/// <c>polyphony-branch-model</c> SKILL).
/// </summary>
/// <remarks>
/// PR #4 observes the per-item impl PR ONLY. The MG roll-up case from
/// closed-loop §3.1 row 5 — an MG item being gated by ALL its impl
/// children's PRs — is deliberately deferred to PR #5 (cross-item
/// rollup). For an MG item with its own merged impl PR but unmerged
/// children, this observer therefore reports
/// <see cref="Disposition.Satisfied"/>; the cross-item reducer will
/// tighten that to the children's worst disposition.
/// </remarks>
/// <param name="Disposition">One of <see cref="Disposition.Needed"/> /
/// <see cref="Disposition.Fulfilling"/> / <see cref="Disposition.Satisfied"/>.
/// The reducer never observes <see cref="Disposition.Ready"/> — that
/// disposition is reducer-derived only.</param>
/// <param name="Reason">Short human-readable diagnostic explaining why the
/// disposition was chosen. Always non-empty so failure modes are
/// self-describing in JSON output.</param>
/// <param name="ImplBranch">The impl branch we inspected (e.g.
/// <c>impl/3043-3050</c>). Empty when the work item or root id was
/// non-positive — the observer degrades to <see cref="Disposition.Needed"/>
/// in that case.</param>
/// <param name="PrNumber">Highest-numbered impl PR for the branch, or null
/// when no PR has been opened yet.</param>
/// <param name="PrUrl">Canonical URL of <see cref="PrNumber"/>, or null.</param>
/// <param name="PrState">Raw <c>gh</c> PR state (<c>OPEN</c> | <c>CLOSED</c>
/// | <c>MERGED</c>), or null when no PR exists or its rich state could
/// not be fetched.</param>
public sealed record ImplementationMergedObservation(
    string Disposition,
    string Reason,
    string ImplBranch,
    int? PrNumber,
    string? PrUrl,
    string? PrState);
