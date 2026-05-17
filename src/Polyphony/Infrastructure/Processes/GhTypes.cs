namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Result of a <c>gh auth status</c> probe. Returns both the bool and the
/// raw detail text (gh writes its happy-path message to stderr so callers
/// that want to surface it for diagnostics need access to the original).
/// </summary>
/// <param name="IsAuthenticated">True when the user is signed in to github.com.</param>
/// <param name="Detail">Human-readable detail string from gh's output. May be empty.</param>
public sealed record GhAuthStatus(bool IsAuthenticated, string Detail);

/// <summary>
/// Subset of fields the workflow scripts pull from <c>gh pr list --json</c>.
/// Add fields here as the union of "what scripts use today" grows.
/// </summary>
/// <param name="Number">PR number.</param>
/// <param name="HeadRefName">Source branch name.</param>
/// <param name="Url">PR web URL. May be null when only number was requested.</param>
/// <param name="MergedAt">When the PR was merged. Null when the PR is open or closed unmerged.</param>
public sealed record PullRequestSummary(
    int Number,
    string HeadRefName,
    string? Url,
    DateTimeOffset? MergedAt);

/// <summary>
/// Filters passed to <see cref="IGhClient.ListPullRequestsAsync"/>. Each
/// non-null property maps to one <c>gh pr list</c> flag.
/// </summary>
/// <param name="Head">Filter to PRs with this head branch name.</param>
/// <param name="Base">Filter to PRs targeting this base branch.</param>
/// <param name="State">Filter by state — typically "open", "merged", or "closed".</param>
/// <param name="Limit">Cap the number of results. gh defaults to 30 when unset.</param>
public sealed record PrListFilters(
    string? Head = null,
    string? Base = null,
    string? State = null,
    int? Limit = null);

/// <summary>
/// Merge method passed to <see cref="IGhClient.MergePullRequestAsync"/>.
/// Maps to the corresponding <c>gh pr merge</c> flag.
/// </summary>
public enum GhMergeMethod
{
    /// <summary>Merge commit. Required for merge-group PRs (see ADR
    /// docs/decisions/branch-model.md) because nested merge groups depend on
    /// git ancestry to know what is already integrated.</summary>
    Merge,

    /// <summary>Squash merge. Default for impl PRs whose micro-history we do
    /// not want to pollute the merge-group branch.</summary>
    Squash,

    /// <summary>Rebase merge. Rarely used; included for completeness.</summary>
    Rebase,
}

/// <summary>
/// Result of <see cref="IGhClient.MergePullRequestAsync"/>. The merge SHA
/// is populated from a follow-up <c>gh pr view</c> call (gh's merge stdout
/// is human-oriented and not reliable). When <see cref="AlreadyMerged"/> is
/// true, the merge succeeded server-side on a prior attempt and the verb
/// reconciled rather than re-issued the merge.
/// </summary>
/// <param name="Succeeded">True when the merge completed (newly or already).</param>
/// <param name="PrNumber">PR number.</param>
/// <param name="MergeSha">Commit SHA of the merge, when known.</param>
/// <param name="AlreadyMerged">True when the PR was already merged before the call.</param>
/// <param name="Detail">Diagnostic detail from gh stdout/stderr.</param>
public sealed record GhMergeResult(
    bool Succeeded,
    int PrNumber,
    string? MergeSha,
    bool AlreadyMerged,
    string Detail);

/// <summary>
/// Subset of fields read by <see cref="IGhClient.GetPullRequestStateAsync"/>
/// from <c>gh pr view --json</c>. The state string mirrors GitHub's enum:
/// <c>OPEN</c>, <c>CLOSED</c>, <c>MERGED</c> (case as returned by gh).
/// </summary>
/// <param name="Number">PR number.</param>
/// <param name="State">PR state (OPEN, CLOSED, MERGED).</param>
/// <param name="MergeCommitSha">Merge commit SHA when merged, otherwise null.</param>
/// <param name="HeadRefName">Source branch name.</param>
/// <param name="HeadRefOid">Current head SHA on the branch.</param>
public sealed record GhPullRequestState(
    int Number,
    string State,
    string? MergeCommitSha,
    string? HeadRefName,
    string? HeadRefOid);

/// <summary>
/// Single review record returned by <c>gh pr view --json reviews</c>.
/// gh's review state values: <c>APPROVED</c>, <c>CHANGES_REQUESTED</c>,
/// <c>COMMENTED</c>, <c>DISMISSED</c>, <c>PENDING</c>.
/// </summary>
/// <param name="Login">Reviewer's GitHub login (or empty when gh omits author).</param>
/// <param name="State">Raw review state from gh.</param>
/// <param name="SubmittedAt">When the review was submitted; null when gh omits it.</param>
public sealed record GhPullRequestReview(
    string Login,
    string State,
    DateTimeOffset? SubmittedAt);

/// <summary>
/// Single PR-level (issue) comment, returned by <c>gh pr view --json comments</c>.
/// Top-level only — review (inline diff) comments are NOT in this list.
/// Used by <c>polyphony pr poll-status</c> to recognize magic-comment
/// approvals (<c>polyphony:approve</c> / <c>polyphony:request-changes</c>)
/// from the PR author when GitHub blocks self-review.
/// </summary>
/// <param name="AuthorLogin">Comment author's GitHub login (or empty when gh omits it).</param>
/// <param name="Body">Raw comment body markdown.</param>
/// <param name="CreatedAt">When the comment was posted; null when gh omits it.</param>
public sealed record GhPullRequestComment(
    string AuthorLogin,
    string Body,
    DateTimeOffset? CreatedAt);

/// <summary>
/// Rich state snapshot returned by <see cref="IGhClient.GetPullRequestPollDataAsync"/>.
/// Captures everything <c>polyphony pr poll-status</c> needs to compose
/// a platform-neutral status without making multiple gh calls.
/// </summary>
/// <param name="Number">PR number.</param>
/// <param name="State">PR state from gh: <c>OPEN</c> | <c>CLOSED</c> | <c>MERGED</c>.</param>
/// <param name="ReviewDecision">gh's aggregated decision: <c>APPROVED</c> | <c>CHANGES_REQUESTED</c> | <c>REVIEW_REQUIRED</c> | empty.</param>
/// <param name="Mergeable">gh's mergeable status: <c>MERGEABLE</c> | <c>CONFLICTING</c> | <c>UNKNOWN</c>.</param>
/// <param name="HeadRefName">Source branch name.</param>
/// <param name="HeadRefOid">Current head SHA on the source branch.</param>
/// <param name="BaseRefName">Target branch name.</param>
/// <param name="MergeCommitSha">Merge commit SHA when state==MERGED, otherwise null.</param>
/// <param name="MergedAt">When the PR was merged; null when not merged.</param>
/// <param name="Body">PR description body — used to parse plan-PR front-matter.</param>
/// <param name="Reviews">All reviews on the PR (oldest first per gh's ordering).</param>
/// <param name="AuthorLogin">PR author's GitHub login (or empty when gh omits it). Used to filter magic-comment approvals.</param>
/// <param name="Comments">All top-level PR comments (issue comments). Empty when gh omits the field. Review (inline) comments are NOT included.</param>
public sealed record GhPullRequestPollData(
    int Number,
    string State,
    string ReviewDecision,
    string Mergeable,
    string? HeadRefName,
    string? HeadRefOid,
    string? BaseRefName,
    string? MergeCommitSha,
    DateTimeOffset? MergedAt,
    string Body,
    IReadOnlyList<GhPullRequestReview> Reviews,
    string AuthorLogin,
    IReadOnlyList<GhPullRequestComment> Comments);

/// <summary>
/// One review thread on a pull request, returned by
/// <see cref="IGhClient.GetPullRequestReviewThreadsAsync"/>. Sourced from
/// the GraphQL <c>pullRequest.reviewThreads</c> connection, which the
/// <c>gh pr view --json</c> surface does NOT expose. After this record's
/// "option B" rewrite (issue #207) review threads are the source of
/// truth for the PR-level <c>changes_requested</c> gate.
/// </summary>
/// <param name="Id">GraphQL node id (opaque string). Stable across polls.</param>
/// <param name="IsResolved">True when the thread has been marked resolved.</param>
/// <param name="IsOutdated">True when the anchored hunk has been rewritten beyond GitHub's recognition. Outdated unresolved threads do NOT block merge.</param>
/// <param name="AuthorLogin">Login of the first comment's author; empty when the platform omits it.</param>
/// <param name="CreatedAt">When the first comment was posted; null when the platform omits it.</param>
/// <param name="CommentCount">Total comments in the thread (GraphQL <c>comments.totalCount</c> when available; otherwise the visible count).</param>
/// <param name="Comments">All visible comments in the thread (oldest first per GraphQL ordering). Empty when the platform returned no nodes.</param>
public sealed record GhReviewThread(
    string Id,
    bool IsResolved,
    bool IsOutdated,
    string AuthorLogin,
    DateTimeOffset? CreatedAt,
    int CommentCount,
    IReadOnlyList<GhReviewThreadComment> Comments);

/// <summary>
/// One comment inside a GitHub review thread, fetched alongside the
/// thread metadata via the GraphQL <c>reviewThreads.nodes.comments</c>
/// connection. The body is the raw markdown the reviewer posted.
/// </summary>
/// <param name="AuthorLogin">Comment author's GitHub login; empty when the platform omits it.</param>
/// <param name="Body">Raw markdown body as posted.</param>
/// <param name="CreatedAt">When the comment was posted; null when the platform omits it.</param>
public sealed record GhReviewThreadComment(
    string AuthorLogin,
    string Body,
    DateTimeOffset? CreatedAt);

/// <summary>
/// Result of <see cref="IGhClient.GetPullRequestReviewThreadsAsync"/>.
/// Threads is the page of threads visible to the verb; <see cref="HasMorePages"/>
/// signals that the PR has more threads than were fetched on this call —
/// the verb fails closed by appending a warning when this is true and no
/// blocking thread was visible (since a blocking thread on a later page
/// would be missed).
/// </summary>
/// <param name="Threads">Visible review threads (oldest first per GraphQL ordering).</param>
/// <param name="HasMorePages">True when the GraphQL <c>pageInfo.hasNextPage</c> indicated more threads exist beyond what was fetched.</param>
public sealed record GhReviewThreadsRead(
    IReadOnlyList<GhReviewThread> Threads,
    bool HasMorePages);

/// <summary>
/// One file changed in a pull request, as reported by
/// <c>gh pr view --json files</c>. Path is repo-relative and uses
/// forward slashes regardless of platform.
/// </summary>
/// <param name="Path">Repo-relative path of the changed file.</param>
/// <param name="Additions">Lines added (-1 if gh did not report).</param>
/// <param name="Deletions">Lines deleted (-1 if gh did not report).</param>
public sealed record GhPullRequestChangedFile(
    string Path,
    int Additions,
    int Deletions);

/// <summary>
/// Discriminator for <see cref="GhEvidenceFloorRead"/>. Lets the
/// Phase 6 evidence-floor verb distinguish "PR genuinely missing" (a
/// 404) from "gh failed for some other reason" — the two map to
/// distinct error codes (<c>pr_not_found</c> vs <c>gh_failed</c>) in
/// the verb's routing-style envelope.
/// </summary>
public enum GhEvidenceFloorOutcome
{
    /// <summary>gh returned a parseable JSON payload — <see cref="GhEvidenceFloorRead.CommitCount"/>
    /// and <see cref="GhEvidenceFloorRead.Body"/> are populated.</summary>
    Found,

    /// <summary>gh exited non-zero with a stderr that looks like a 404
    /// ("could not resolve" / "no pull requests found").</summary>
    PrNotFound,

    /// <summary>gh failed for any other reason (auth missing,
    /// timeout-exhausted, malformed JSON, network error). The verb
    /// surfaces <see cref="GhEvidenceFloorRead.Detail"/> in its
    /// error_message envelope.</summary>
    GhFailed,
}

/// <summary>
/// Result of <see cref="IGhClient.GetPullRequestEvidenceFloorAsync"/>.
/// Always non-null — failure modes are conveyed via
/// <see cref="Outcome"/> rather than null sentinels because the caller
/// (a routing-style verb) needs to distinguish "not found" from
/// "transport error" deterministically.
/// </summary>
/// <param name="Outcome">Discriminator (see <see cref="GhEvidenceFloorOutcome"/>).</param>
/// <param name="CommitCount">Number of commits on the PR's head branch beyond base. Zero when not <see cref="GhEvidenceFloorOutcome.Found"/>.</param>
/// <param name="Body">Raw PR body (untrimmed). Empty when not <see cref="GhEvidenceFloorOutcome.Found"/>.</param>
/// <param name="Detail">Human-readable diagnostic detail (typically gh stderr trimmed). Null when <see cref="GhEvidenceFloorOutcome.Found"/>.</param>
public sealed record GhEvidenceFloorRead(
    GhEvidenceFloorOutcome Outcome,
    int CommitCount,
    string Body,
    string? Detail);
