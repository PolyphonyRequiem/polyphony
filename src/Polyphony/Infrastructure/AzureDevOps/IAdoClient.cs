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
}
