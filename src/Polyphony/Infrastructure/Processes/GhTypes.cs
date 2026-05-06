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

    /// <summary>Squash merge. Default for task PRs whose micro-history we do
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
    IReadOnlyList<GhPullRequestReview> Reviews);
