namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Typed wrapper over the Azure DevOps REST API. Phase 5 scaffolding —
/// currently exposes only the auth probe. Pull-request, review, and vote
/// operations will land in subsequent PRs and extend this interface.
///
/// <para>
/// This is polyphony's own ADO client (deliberately not a dependency on the
/// twig2 ADO infrastructure). The wire-level concerns (PAT resolution, Basic
/// auth header format, HTTP timeout/retry policy) borrow the established
/// pattern from <c>Twig.Infrastructure.Ado.AdoRestClient</c> but the verbs
/// will be polyphony-specific.
/// </para>
///
/// <para>
/// Failure semantics mirror <see cref="Polyphony.Infrastructure.Processes.IGhClient"/>:
/// every method enforces a timeout + retry policy (see
/// <see cref="AdoClientPolicy"/>) so a hung dev.azure.com call cannot stall
/// the orchestrator indefinitely. Probe-style methods report failure via the
/// result type rather than throwing, so preflight can degrade gracefully.
/// </para>
/// </summary>
public interface IAdoClient
{
    /// <summary>
    /// Probe the configured PAT against
    /// <c>https://dev.azure.com/_apis/connectionData?api-version=7.1</c>.
    ///
    /// <para>
    /// Best-effort: does not throw. Outcomes are encoded in
    /// <see cref="AdoAuthStatus"/>:
    /// <list type="bullet">
    ///   <item>200 + parseable body → <c>IsAuthenticated = true</c>;
    ///     <see cref="AdoAuthStatus.OrganizationName"/> is set to the
    ///     authenticated user's provider display name.</item>
    ///   <item>No PAT configured → <c>IsAuthenticated = false</c> with a
    ///     "no PAT configured" detail pointing at the env var and az fallback.</item>
    ///   <item>401 / 403 → <c>IsAuthenticated = false</c> with a
    ///     "PAT rejected" detail.</item>
    ///   <item>Timeout (every attempt exhausted) → <c>IsAuthenticated = false</c>
    ///     with a "timed out probing dev.azure.com" detail.</item>
    ///   <item>Any other error (network, malformed JSON, unexpected status)
    ///     → <c>IsAuthenticated = false</c> with a "Probe failed: ..."
    ///     detail.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="ct">Caller cancellation token; propagated immediately.</param>
    Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// List pull requests in the given repository, optionally filtered by status.
    ///
    /// <para>
    /// Hits <c>GET /_apis/git/repositories/{repo}/pullrequests</c>. Returns an
    /// empty list when the repository has no PRs matching the filter, and
    /// <c>null</c> when the repository (or project) does not exist (HTTP 404).
    /// </para>
    ///
    /// <para>
    /// Throws on HTTP errors that indicate a configuration problem rather than
    /// missing data:
    /// <list type="bullet">
    ///   <item><see cref="HttpRequestException"/> for 401/403 (PAT rejected),
    ///     5xx (server error — not retried, see
    ///     <see cref="AdoClientPolicy"/>), and any other non-success status.</item>
    ///   <item><see cref="TimeoutException"/> when every retry attempt timed
    ///     out.</item>
    ///   <item><see cref="InvalidOperationException"/> when no PAT is
    ///     configured.</item>
    /// </list>
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
        string organization,
        string project,
        string repository,
        AdoPullRequestStatus status = AdoPullRequestStatus.Active,
        string? sourceBranch = null,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch a single pull request by ID.
    ///
    /// <para>
    /// Hits <c>GET /_apis/git/repositories/{repo}/pullrequests/{prId}</c>.
    /// Returns <c>null</c> when the PR (or repo/project) does not exist
    /// (HTTP 404). Throws on other failures — see
    /// <see cref="ListPullRequestsAsync"/> for the failure shape.
    /// </para>
    /// </summary>
    Task<AdoPullRequest?> GetPullRequestAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    /// Create a new pull request.
    ///
    /// <para>
    /// Hits <c>POST /_apis/git/repositories/{repo}/pullrequests</c>. Branch
    /// names are normalized: a short branch (<c>"feature/x"</c>) is rewritten
    /// to a full ref (<c>"refs/heads/feature/x"</c>); a value already prefixed
    /// with <c>"refs/"</c> is passed through unchanged.
    /// </para>
    ///
    /// <para>
    /// Returns the created PR. Returns <c>null</c> when the repository does
    /// not exist (HTTP 404). Throws on other failures — see
    /// <see cref="ListPullRequestsAsync"/> for the failure shape.
    /// </para>
    ///
    /// <para>
    /// <b>Description length.</b> ADO rejects descriptions longer than
    /// <see cref="AdoConstants.MaxPullRequestDescriptionLength"/> (4000) with
    /// HTTP 400. The implementation truncates oversize descriptions at the
    /// infrastructure boundary (with a visible marker) so callers do not need
    /// to. Callers that need overflow preserved (e.g. as a comment thread)
    /// must trim before calling and post the remainder via
    /// <see cref="CreatePullRequestCommentThreadAsync"/> themselves.
    /// </para>
    /// </summary>
    /// <param name="sourceBranch">
    /// Source ref. Either a full ref (<c>refs/heads/feature/x</c>) or a short
    /// branch name (<c>feature/x</c>); the short form is normalized.
    /// </param>
    /// <param name="targetBranch">
    /// Target ref. Same normalization rules as <paramref name="sourceBranch"/>.
    /// </param>
    Task<AdoPullRequest?> CreatePullRequestAsync(
        string organization,
        string project,
        string repository,
        string sourceBranch,
        string targetBranch,
        string title,
        string description,
        CancellationToken ct = default);

    /// <summary>
    /// Compose the platform-neutral PR poll snapshot consumed by
    /// <c>polyphony pr poll-status-ado</c>. Mirrors
    /// <see cref="Polyphony.Infrastructure.Processes.IGhClient.GetPullRequestPollDataAsync"/>
    /// so the verb output is identical regardless of platform.
    ///
    /// <para>
    /// Composes the result from two ADO REST calls:
    /// <list type="bullet">
    ///   <item><c>GET /_apis/git/repositories/{repo}/pullrequests/{prId}</c> —
    ///     PR detail (state, refs, last merge source/commit, body).</item>
    ///   <item><c>GET /_apis/git/repositories/{repo}/pullrequests/{prId}/reviewers</c> —
    ///     reviewer list with the vote enum.</item>
    /// </list>
    /// Both calls go through <see cref="AdoClientPolicy"/> for retry/timeout
    /// shaping. Returns <c>null</c> when the PR detail call returns 404 (the
    /// reviewers call is skipped in that case).
    /// </para>
    ///
    /// <para>Throws on the same conditions as
    /// <see cref="ListPullRequestsAsync"/>: <see cref="HttpRequestException"/>
    /// for 401/403/5xx, <see cref="TimeoutException"/> when retries are
    /// exhausted, and <see cref="InvalidOperationException"/> when no PAT is
    /// configured.</para>
    /// </summary>
    /// <param name="repositoryId">
    /// Repository identifier — either the GUID or the (URL-safe) name. Both
    /// are accepted by the ADO REST endpoint.
    /// </param>
    Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
        string organization,
        string project,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    /// Overload that opts the review-decision aggregator into permissive
    /// mode — any reviewer's positive vote (+5 / +10) counts as APPROVED,
    /// not just required-reviewer votes. See
    /// <see cref="AdoClient.AggregateReviewDecision(IReadOnlyList{AdoReviewerRaw}, bool)"/>
    /// for the full semantic + stale-approval caveat. Default
    /// implementation delegates to the strict overload, so fakes and
    /// callers that don't care about the flag get the original behaviour
    /// for free.
    /// </summary>
    /// <param name="allowAnyApprovalVote">
    /// When true, the aggregator returns APPROVED whenever ANY reviewer
    /// cast a positive vote with no rejection present. Default false
    /// preserves strict required-reviewer aggregation.
    /// </param>
    Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
        string organization,
        string project,
        string repositoryId,
        int pullRequestId,
        bool allowAnyApprovalVote,
        CancellationToken ct = default)
        => GetPullRequestPollDataAsync(organization, project, repositoryId, pullRequestId, ct);

    /// <summary>
    /// Submit (or update) a reviewer's vote on an ADO pull request.
    ///
    /// <para>
    /// Hits <c>PATCH /_apis/git/repositories/{repo}/pullRequests/{pr}/reviewers/{reviewerId}</c>
    /// with body <c>{ "vote": &lt;int&gt; }</c>. The reviewer must already be on
    /// the PR (use <c>PUT</c> on the same endpoint to add). Returns
    /// <c>true</c> on success (HTTP 200) and <c>false</c> when the PR or
    /// reviewer does not exist (HTTP 404). Throws on other failures — see
    /// <see cref="ListPullRequestsAsync"/> for the failure shape.
    /// </para>
    ///
    /// <para>
    /// Vote values per the ADO REST contract:
    /// <list type="bullet">
    ///   <item><c>10</c> — approved.</item>
    ///   <item><c>5</c> — approved with suggestions.</item>
    ///   <item><c>0</c> — no vote (reset).</item>
    ///   <item><c>-5</c> — waiting for author.</item>
    ///   <item><c>-10</c> — rejected.</item>
    /// </list>
    /// Other values are passed through unchanged; ADO will reject them with
    /// a 400 (which surfaces as <see cref="HttpRequestException"/>).
    /// </para>
    /// </summary>
    /// <param name="reviewerId">Reviewer's identity GUID.</param>
    /// <param name="vote">ADO vote enum value (see method summary).</param>
    Task<bool> SetPullRequestVoteAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string reviewerId,
        int vote,
        CancellationToken ct = default);

    /// <summary>
    /// Complete (merge) an Azure DevOps pull request — the ADO equivalent of
    /// <c>gh pr merge --merge --match-head-commit &lt;sha&gt;</c>.
    /// Mirrors the GitHub-side merge step inside <c>polyphony pr merge-plan-pr</c>;
    /// the new <c>polyphony pr merge-plan-ado</c> verb (Phase 5) consumes this
    /// to perform the platform half of the compound transactional verb.
    ///
    /// <para>
    /// Hits <c>PATCH /_apis/git/repositories/{repo}/pullRequests/{pr}?api-version=7.1</c>
    /// with body
    /// <c>{ status: "completed", lastMergeSourceCommit: { commitId: &lt;headSha&gt; },
    /// completionOptions: { mergeStrategy: "noFastForward",
    /// deleteSourceBranch: false, bypassPolicy: false } }</c>.
    /// Per ADR Rev 4 the strategy is pinned to <c>noFastForward</c>
    /// (preserves a real merge commit so sibling plan branches can be
    /// reasoned about by SHA) and source-branch deletion is disabled (other
    /// plan branches may still be in flight).
    /// </para>
    ///
    /// <para>
    /// <b>Stale-head guard.</b> The supplied <paramref name="lastMergeSourceCommitSha"/>
    /// is ADO's analogue of <c>gh pr merge --match-head-commit</c>. When the
    /// PR's source branch has advanced past that SHA (someone pushed between
    /// poll and merge), ADO refuses with HTTP 409. The verb returns
    /// <see cref="AdoCompletePullRequestResult.Status"/> = <c>"stale_head"</c>
    /// rather than throwing so the calling verb can route to a re-poll-and-retry.
    /// </para>
    ///
    /// <para>
    /// <b>Completion confirmation.</b> ADO's PATCH is asynchronous on the
    /// server: 200 OK means "merge request accepted", NOT "merge landed".
    /// The implementation confirms completion by checking that the PR's
    /// <c>status</c> has flipped to <c>"completed"</c> AND
    /// <c>lastMergeCommit.commitId</c> is populated. While the PR is
    /// active, ADO routinely populates <c>lastMergeCommit</c> with the
    /// preview merge SHA (what the merge would be if completion ran right
    /// now), so that field alone is NOT proof of completion. If the PR
    /// remains active for the full poll budget the verb returns
    /// <c>"completion_pending"</c> so the caller can route the operator to
    /// manual recovery rather than report a fake merge SHA.
    /// </para>
    ///
    /// <para>
    /// Failure shape, encoded into <see cref="AdoCompletePullRequestResult.Status"/>:
    /// <list type="bullet">
    ///   <item><c>"completed"</c> — HTTP 200 AND the PR detail confirms <c>status == "completed"</c> AND <c>lastMergeCommit.commitId</c> populated; <c>MergeCommitSha</c> carries the landed commit.</item>
    ///   <item><c>"completion_pending"</c> — HTTP 200 to the PATCH, but the PR did not transition to <c>status == "completed"</c> within the poll budget (or vanished mid-poll). The merge MAY still land asynchronously; treat as a failure for routing purposes and direct the operator to inspect the PR (e.g. <c>az repos pr show --id N</c>) and optionally complete it manually.</item>
    ///   <item><c>"stale_head"</c> — HTTP 409 (source branch advanced past <paramref name="lastMergeSourceCommitSha"/>).</item>
    ///   <item><c>"not_found"</c> — HTTP 404 (PR or repo missing).</item>
    ///   <item><c>"not_mergeable"</c> — HTTP 400 (ADO refused for a non-stale reason — policy block, conflicts, …).</item>
    ///   <item><c>"ado_error"</c> — any other non-success status that doesn't trigger the cases above.</item>
    /// </list>
    /// Throws on the same conditions as <see cref="ListPullRequestsAsync"/>:
    /// <see cref="HttpRequestException"/> for 401/403/5xx (after retries
    /// exhausted), <see cref="TimeoutException"/> when retries are
    /// exhausted, and <see cref="InvalidOperationException"/> when no PAT
    /// is configured.
    /// </para>
    /// </summary>
    /// <param name="lastMergeSourceCommitSha">
    /// Head SHA of the source branch as observed by the caller's pre-merge
    /// poll. ADO compares this to its current source tip and refuses with
    /// HTTP 409 (surfaced as <c>"stale_head"</c>) when they differ — the
    /// stale-head guard.
    /// </param>
    /// <param name="mergeStrategy">
    /// Wire-level merge strategy. Plan/MG PRs pass
    /// <see cref="AdoMergeStrategy.NoFastForward"/> (per ADR Rev 4 — preserves
    /// the merge commit). Impl PRs pass <see cref="AdoMergeStrategy.Squash"/>
    /// (matches the GitHub-side <c>gh pr merge --squash</c>). The enum is
    /// translated to ADO's wire-level strings (<c>noFastForward</c>,
    /// <c>squash</c>, <c>rebase</c>, <c>rebaseMerge</c>) inside the impl.
    /// </param>
    /// <param name="deleteSourceBranch">
    /// True ⇒ ADO deletes the source ref as part of the completion. Plan PRs
    /// pass <c>false</c> because sibling plan branches may still be in
    /// flight; impl PRs pass <c>true</c> to clean up the per-MG impl branch
    /// after squash.
    /// </param>
    Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string lastMergeSourceCommitSha,
        AdoMergeStrategy mergeStrategy,
        bool deleteSourceBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Post a single advisory comment thread to an Azure DevOps pull request
    /// — the ADO equivalent of
    /// <c>gh pr review {prNumber} --comment --body "&lt;body&gt;"</c>.
    ///
    /// <para>
    /// Hits <c>POST /_apis/git/repositories/{repo}/pullRequests/{pr}/threads</c>
    /// with body <c>{ comments: [ { parentCommentId: 0, content: &lt;body&gt;,
    /// commentType: 1 } ], status: 4 }</c> — a top-level text comment in a
    /// closed thread (no follow-up expected). Returns the created thread's
    /// ID and the inner comment ID on success. Returns <c>null</c> when the
    /// PR or repository does not exist (HTTP 404). Throws on other failures
    /// — see <see cref="ListPullRequestsAsync"/> for the failure shape.
    /// </para>
    /// </summary>
    /// <param name="commentBody">The Markdown comment body (must be non-empty).</param>
    Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string commentBody,
        CancellationToken ct = default);

    /// <summary>
    /// Harvest the human-authored review comments from an Azure DevOps pull
    /// request — the read-side counterpart to
    /// <see cref="CreatePullRequestCommentThreadAsync"/>, and the closing
    /// piece of ADO PR remediation parity tracked in
    /// <c>docs/decisions/ado-feature-pr-parity.md</c>.
    ///
    /// <para>
    /// Hits <c>GET /_apis/git/repositories/{repo}/pullRequests/{pr}/threads?api-version=7.1</c>.
    /// The response is filtered before mapping to the public projection:
    /// <list type="bullet">
    ///   <item>Threads with <c>isDeleted == true</c> are dropped entirely.</item>
    ///   <item>Comments with <c>isDeleted == true</c> are dropped from each
    ///     thread.</item>
    ///   <item>Comments with <c>commentType == "system"</c> (auto-generated
    ///     "branch updated", "policy reset", …) are dropped — the verb
    ///     surfaces only human-authored content.</item>
    ///   <item>Threads whose surviving comments list is empty after the
    ///     above filters are dropped.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Returns the projected threads in ADO's response order; comments
    /// inside each thread keep ADO's order (oldest first). Returns
    /// <c>null</c> when the PR or repository does not exist (HTTP 404)
    /// so the calling verb can emit <c>pr_not_found</c>. Throws on other
    /// failures — see <see cref="ListPullRequestsAsync"/> for the failure
    /// shape.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    /// Compose the platform-neutral evidence-floor read for an ADO pull
    /// request — the ADO analogue of
    /// <see cref="Polyphony.Infrastructure.Processes.IGhClient.GetPullRequestEvidenceFloorAsync"/>.
    ///
    /// <para>
    /// Composes the result from two ADO REST calls:
    /// <list type="bullet">
    ///   <item><c>GET /_apis/git/repositories/{repo}/pullrequests/{prId}</c> —
    ///     PR detail (for the description / "body").</item>
    ///   <item><c>GET /_apis/git/repositories/{repo}/pullrequests/{prId}/commits</c>
    ///     — commits on the source branch beyond base (for the commit count).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Returns a discriminated <see cref="AdoEvidenceFloorRead"/> so the
    /// calling verb can distinguish "PR genuinely missing" (404 →
    /// <see cref="AdoEvidenceFloorOutcome.PrNotFound"/>) from "ADO failed for
    /// some other reason" (timeout, malformed JSON, network error →
    /// <see cref="AdoEvidenceFloorOutcome.AdoFailed"/>) and route them to
    /// distinct error codes (<c>pr_not_found</c> vs <c>ado_failed</c>) in
    /// its envelope. Mirrors the failure-shape contract of the GitHub
    /// sibling exactly so the workflow neutral-normalization step can
    /// treat both legs identically.
    /// </para>
    /// </summary>
    Task<AdoEvidenceFloorRead> GetPullRequestEvidenceFloorAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    /// Return the list of files changed by an ADO pull request — the ADO
    /// analogue of
    /// <see cref="Polyphony.Infrastructure.Processes.IGhClient.GetPullRequestFilesAsync"/>.
    /// Used by <c>polyphony pr validate-plan-diff</c> and the merge-time
    /// guard in <c>polyphony pr merge-plan-pr</c>'s ADO sibling to classify
    /// whether a child plan PR touched parent / ancestor / polyphony-state
    /// files.
    ///
    /// <para>
    /// Hits <c>GET /_apis/git/repositories/{repo}/pullrequests/{prId}/iterations</c>
    /// to find the latest iteration, then
    /// <c>GET /_apis/git/repositories/{repo}/pullrequests/{prId}/iterations/{iter}/changes</c>
    /// to read the per-file change list. Returns <c>null</c> when the PR
    /// (or repo) does not exist (HTTP 404). Throws on other failures — see
    /// <see cref="ListPullRequestsAsync"/> for the failure shape.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AdoPullRequestChangedFile>?> GetPullRequestFilesAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    /// Replace the description (= body) of an existing ADO pull request —
    /// the ADO analogue of
    /// <see cref="Polyphony.Infrastructure.Processes.IGhClient.EditPullRequestBodyAsync"/>.
    ///
    /// <para>
    /// Hits <c>PATCH /_apis/git/repositories/{repo}/pullrequests/{prId}</c>
    /// with body <c>{ description: &lt;body&gt; }</c>.
    /// <b>ADO field name is <c>description</c>, not <c>body</c></b> (per
    /// the REST contract: <c>git/pullrequests/update</c>).
    /// </para>
    ///
    /// <para>
    /// Used by the cascade remedy
    /// (<c>polyphony plan rebase-stale-descendant</c>) to rewrite the
    /// <c>ancestor_plan_generations</c> front-matter snapshot after a
    /// successful auto-rebase. Body-edit failure on that path is a
    /// recoverable, routable outcome — not a fatal exception — so this
    /// method <b>returns false on any failure</b> (non-success status,
    /// timeout, network error) and never throws
    /// <see cref="HttpRequestException"/> /
    /// <see cref="TimeoutException"/>. Caller-driven cancellation still
    /// propagates as <see cref="OperationCanceledException"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Length limit.</b> ADO rejects descriptions longer than
    /// <see cref="AdoConstants.MaxPullRequestDescriptionLength"/> characters
    /// (4000) with HTTP 400. The implementation truncates oversize
    /// descriptions at the infrastructure boundary (with a visible marker) so
    /// callers do not need to. Callers that need overflow preserved (e.g. as
    /// a comment thread) must trim before calling and post the remainder via
    /// <see cref="CreatePullRequestCommentThreadAsync"/> themselves.
    /// </para>
    /// </summary>
    Task<bool> EditPullRequestBodyAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// Abandon an open ADO pull request, optionally posting a final comment
    /// thread first — the ADO analogue of
    /// <see cref="Polyphony.Infrastructure.Processes.IGhClient.ClosePullRequestAsync"/>.
    ///
    /// <para>
    /// Sequence: when <paramref name="commentBeforeClose"/> is non-empty,
    /// first POST a comment thread via
    /// <see cref="CreatePullRequestCommentThreadAsync"/>; comment failure is
    /// best-effort (logged, does not abort the close). Then PATCH the PR
    /// with <c>{ status: "abandoned" }</c>.
    /// </para>
    ///
    /// <para>
    /// Used by the cascade-remedy <c>recreate</c> path
    /// (<c>polyphony plan recreate-stale-descendant</c>) to abandon a stale
    /// plan PR before a fresh PR is opened from the new ancestor tip.
    /// Returns <c>false</c> on any non-success outcome (404, timeout,
    /// network error). Idempotent: PATCHing an already-abandoned PR returns
    /// 200 (ADO accepts the no-op write).
    /// </para>
    /// </summary>
    Task<bool> ClosePullRequestAsync(
        string organization,
        string project,
        string repository,
        int pullRequestId,
        string commentBeforeClose,
        CancellationToken ct = default);
}
