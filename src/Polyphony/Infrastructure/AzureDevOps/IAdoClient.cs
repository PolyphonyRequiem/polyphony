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
}
