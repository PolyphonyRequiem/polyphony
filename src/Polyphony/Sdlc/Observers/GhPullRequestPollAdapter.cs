using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Bridge from <see cref="AdoPullRequestPollData"/> to
/// <see cref="GhPullRequestPollData"/> so the existing platform-neutral
/// mappers in <see cref="PlanObserver"/> (PlanAuthored / PlanReviewed /
/// PlanPromoted / ImplementationMerged) can accept ADO PR snapshots
/// without needing a separate type-per-platform branch in every consumer.
///
/// <para>
/// The two record shapes already share a NEUTRAL state vocabulary
/// (<c>OPEN | MERGED | CLOSED</c>) and a NEUTRAL review-decision
/// vocabulary (<c>APPROVED | REVIEW_REQUIRED | REJECTED</c>) — see
/// <see cref="AdoPullRequestPollData"/>'s XML doc, which calls this out
/// explicitly. The adapter is therefore a structural projection: every
/// field maps 1:1 except for the handful of GH-only signals (Reviews
/// detail, Body comments, AuthorLogin) which are projected with the
/// closest ADO equivalent.
/// </para>
///
/// <para>
/// Lossy fields:
/// <list type="bullet">
///   <item><c>Comments</c> — ADO does not surface PR-level issue comments
///     in its single-PR detail call. The adapter emits an empty list; the
///     <c>polyphony pr poll-status</c> magic-comment path that uses this
///     field on the GitHub side is intentionally not supported on ADO
///     (operators use ADO's own approval workflow).</item>
///   <item><c>AuthorLogin</c> — ADO's <c>createdBy.displayName</c> is the
///     closest analogue and is what already lands in
///     <see cref="AdoPullRequestPollData"/>'s reviewer identities.</item>
///   <item><c>Reviews[].Login</c> ↔ <c>AdoPullRequestReview.Identity</c>;
///     <c>Reviews[].State</c> ↔ <c>AdoPullRequestReview.Vote</c> (already
///     normalised to a stable lower-snake vocabulary on ingest);
///     <c>Reviews[].SubmittedAt</c> stays null (ADO does not report it).</item>
/// </list>
/// </para>
/// </summary>
internal static class GhPullRequestPollAdapter
{
    public static GhPullRequestPollData FromAdo(AdoPullRequestPollData ado)
    {
        ArgumentNullException.ThrowIfNull(ado);

        var reviews = new List<GhPullRequestReview>(ado.Reviews.Count);
        foreach (var r in ado.Reviews)
        {
            reviews.Add(new GhPullRequestReview(
                Login: r.Identity,
                State: NormalizeAdoVoteToGhReviewState(r.Vote),
                SubmittedAt: r.SubmittedAt is null ? null : new DateTimeOffset(r.SubmittedAt.Value, TimeSpan.Zero)));
        }

        return new GhPullRequestPollData(
            Number: ado.Number,
            State: ado.State,
            ReviewDecision: ado.ReviewDecision,
            Mergeable: ado.Mergeable,
            HeadRefName: ado.HeadRefName,
            HeadRefOid: string.IsNullOrEmpty(ado.HeadRefOid) ? null : ado.HeadRefOid,
            BaseRefName: ado.BaseRefName,
            MergeCommitSha: ado.MergeCommit,
            MergedAt: ado.MergedAt is null ? null : new DateTimeOffset(ado.MergedAt.Value, TimeSpan.Zero),
            Body: ado.Body,
            Reviews: reviews,
            AuthorLogin: string.Empty,
            Comments: Array.Empty<GhPullRequestComment>());
    }

    /// <summary>
    /// Translate ADO's normalised vote vocabulary
    /// (<c>approved | approved_with_suggestions | no_vote | waiting_for_author | rejected</c>)
    /// to GitHub's review-state vocabulary
    /// (<c>APPROVED | COMMENTED | DISMISSED | CHANGES_REQUESTED | PENDING</c>).
    /// The mapping is lossy in both directions; the table here picks the
    /// closest analogue used by downstream PR-level reducers.
    /// </summary>
    private static string NormalizeAdoVoteToGhReviewState(string adoVote) => adoVote switch
    {
        "approved" => "APPROVED",
        "approved_with_suggestions" => "APPROVED",
        "no_vote" => "PENDING",
        "waiting_for_author" => "CHANGES_REQUESTED",
        "rejected" => "CHANGES_REQUESTED",
        _ => "COMMENTED",
    };
}
