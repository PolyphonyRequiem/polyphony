namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Typed wrapper over the <c>gh</c> (GitHub) CLI. Consolidates the
/// authentication probe and pull-request operations the workflow scripts
/// rely on.
///
/// <para>
/// Failure semantics differ per method, but every method enforces a
/// timeout + retry policy (see <see cref="GhClientPolicy"/>) so a hanging
/// <c>gh</c> process cannot stall the orchestrator indefinitely.
/// </para>
///
/// <list type="bullet">
///   <item><see cref="GetAuthStatusAsync"/>: best-effort probe. On timeout,
///     returns an unauthenticated status with a "timed out" detail rather
///     than throwing — preflight should be able to report and remediate.</item>
///   <item><see cref="ListPullRequestsAsync"/>: returns empty on benign
///     non-zero exits (auth missing, repo unset, malformed JSON). <b>Throws
///     <see cref="ExternalToolTimeoutException"/> on timeout</b> so create-flow
///     gates do not silently confuse "I have no idea" with "no PR exists".</item>
///   <item><see cref="CreatePullRequestAsync"/>: throws
///     <see cref="ExternalToolException"/> for real errors (validation, branch
///     missing). On timeout, reconciles against an existing open PR for the
///     same (head, base) before retrying — a timed-out attempt may have been
///     accepted server-side. If reconciliation finds the PR, returns its URL;
///     otherwise retries until the policy is exhausted, then throws
///     <see cref="ExternalToolTimeoutException"/>.</item>
/// </list>
/// </summary>
public interface IGhClient
{
    /// <summary>
    /// <c>gh auth status</c>. gh emits its happy-path message to stderr,
    /// so the result type carries both a bool and the raw detail. On
    /// timeout, returns <c>IsAuthenticated = false</c> with a "timed out"
    /// detail (does not throw).
    /// </summary>
    Task<GhAuthStatus> GetAuthStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr list --repo {repoSlug} --json number,headRefName,url,mergedAt [filters]</c>.
    /// Returns empty when the call returns a non-zero exit (matches the
    /// existing <c>Invoke-GH</c> contract). Throws
    /// <see cref="ExternalToolTimeoutException"/> when every attempt
    /// exceeded the per-attempt timeout — callers that want "treat hang
    /// as no PRs" must explicitly catch this.
    /// </summary>
    Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(
        string repoSlug,
        PrListFilters filters,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr create --repo {repoSlug} --base {base} --head {head} --title {title} --body {body}</c>.
    /// Returns the created PR's URL on success. Reconciles against an
    /// existing open PR for the same (head, base) on timeout and on
    /// "already exists" stderr — returns that PR's URL when a server-side
    /// duplicate is found. Throws <see cref="ExternalToolException"/> for
    /// real errors and <see cref="ExternalToolTimeoutException"/> when
    /// every attempt timed out without reconciliation finding the PR.
    /// </summary>
    Task<string> CreatePullRequestAsync(
        string repoSlug,
        string baseBranch,
        string headBranch,
        string title,
        string body,
        CancellationToken ct = default);
}
