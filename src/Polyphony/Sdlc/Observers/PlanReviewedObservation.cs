namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Observation of the <c>plan_reviewed</c> requirement kind. Produced by
/// <see cref="PlanObserver.ObservePlanReviewedAsync"/> from the latest plan
/// PR's review decision.
/// </summary>
/// <param name="Disposition">
/// <list type="bullet">
///   <item><see cref="Disposition.Satisfied"/> when the PR is OPEN-and-APPROVED
///     or MERGED (a merged PR is implicitly past review).</item>
///   <item><see cref="Disposition.Fulfilling"/> when the PR is OPEN with a
///     non-APPROVED review decision (REVIEW_REQUIRED / CHANGES_REQUESTED).</item>
///   <item><see cref="Disposition.Needed"/> when no PR exists, or when the
///     PR is CLOSED unmerged (review effort must restart).</item>
/// </list>
/// </param>
/// <param name="Reason">Short human-readable diagnostic.</param>
/// <param name="PrNumber">Highest-numbered plan PR for the branch, or null.</param>
/// <param name="PrUrl">Canonical URL of <see cref="PrNumber"/>, or null.</param>
/// <param name="PrState">Raw <c>gh</c> PR state, or null.</param>
/// <param name="ReviewDecision">gh's aggregated review decision
/// (<c>APPROVED</c> | <c>CHANGES_REQUESTED</c> | <c>REVIEW_REQUIRED</c> |
/// empty), or null when no PR exists.</param>
public sealed record PlanReviewedObservation(
    string Disposition,
    string Reason,
    int? PrNumber,
    string? PrUrl,
    string? PrState,
    string? ReviewDecision);
