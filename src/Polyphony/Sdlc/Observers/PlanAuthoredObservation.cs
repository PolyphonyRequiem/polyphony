namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Observation of the <c>plan_authored</c> requirement kind for a single
/// work item. Produced by <see cref="PlanObserver.ObservePlanAuthoredAsync"/>
/// from the live state of <c>origin/{plan_branch}</c> and the latest plan PR.
/// </summary>
/// <param name="Disposition">One of <see cref="Disposition.Needed"/> /
/// <see cref="Disposition.Fulfilling"/> / <see cref="Disposition.Satisfied"/>.
/// The reducer never observes <see cref="Disposition.Ready"/> — that
/// disposition is reducer-derived only.</param>
/// <param name="Reason">Short human-readable diagnostic explaining why the
/// disposition was chosen. Always non-empty so failure modes are
/// self-describing in JSON output.</param>
/// <param name="PlanBranch">The plan branch we inspected (e.g.
/// <c>plan/100-101</c>). Always populated.</param>
/// <param name="BranchExistsOnOrigin">True when
/// <c>git ls-remote --heads origin {plan_branch}</c> returned a hit.
/// False on absence or on transient ls-remote failures (failures degrade
/// to "not observed" rather than throw — callers that want to distinguish
/// must call the lower-level primitive on <see cref="PlanObserver"/>).</param>
/// <param name="PrNumber">Highest-numbered plan PR for the branch, or null
/// when no PR has been opened yet.</param>
/// <param name="PrUrl">Canonical URL of <see cref="PrNumber"/>, or null.</param>
/// <param name="PrState">Raw <c>gh</c> PR state (<c>OPEN</c> | <c>CLOSED</c>
/// | <c>MERGED</c>), or null when no PR exists.</param>
public sealed record PlanAuthoredObservation(
    string Disposition,
    string Reason,
    string PlanBranch,
    bool BranchExistsOnOrigin,
    int? PrNumber,
    string? PrUrl,
    string? PrState);
