namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Observation of the <c>plan_promoted</c> requirement kind. Produced by
/// <see cref="PlanObserver.ObservePlanPromotedAsync"/> from the latest plan
/// PR's merge state.
/// </summary>
/// <param name="Disposition">
/// <list type="bullet">
///   <item><see cref="Disposition.Satisfied"/> when the PR is MERGED.</item>
///   <item><see cref="Disposition.Fulfilling"/> when the PR is OPEN.</item>
///   <item><see cref="Disposition.Needed"/> when no PR exists or the PR is
///     CLOSED without merging.</item>
/// </list>
/// </param>
/// <param name="Reason">Short human-readable diagnostic.</param>
/// <param name="PrNumber">Highest-numbered plan PR for the branch, or null.</param>
/// <param name="PrUrl">Canonical URL of <see cref="PrNumber"/>, or null.</param>
/// <param name="PrState">Raw <c>gh</c> PR state (<c>OPEN</c> | <c>CLOSED</c>
/// | <c>MERGED</c>), or null.</param>
public sealed record PlanPromotedObservation(
    string Disposition,
    string Reason,
    int? PrNumber,
    string? PrUrl,
    string? PrState);
