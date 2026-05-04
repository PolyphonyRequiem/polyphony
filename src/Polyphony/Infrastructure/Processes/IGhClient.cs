namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Typed wrapper over the <c>gh</c> (GitHub) CLI. Consolidates the
/// authentication probe and pull-request operations the workflow scripts
/// rely on.
///
/// All methods that return collections return empty lists on benign
/// failures (auth missing, repo unset). Throws
/// <see cref="ExternalToolException"/> for unrecoverable errors such as
/// PR creation failure with a real error payload.
/// </summary>
public interface IGhClient
{
    /// <summary>
    /// <c>gh auth status</c>. gh emits its happy-path message to stderr,
    /// so the result type carries both a bool and the raw detail.
    /// </summary>
    Task<GhAuthStatus> GetAuthStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr list --repo {repoSlug} --json number,headRefName,url,mergedAt [filters]</c>.
    /// Returns empty when the call fails (matches the existing
    /// <c>Invoke-GH</c> contract, which returns null on non-zero exit).
    /// </summary>
    Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(
        string repoSlug,
        PrListFilters filters,
        CancellationToken ct = default);

    /// <summary>
    /// <c>gh pr create --repo {repoSlug} --base {base} --head {head} --title {title} --body {body}</c>.
    /// Returns the created PR's URL on success. Throws
    /// <see cref="ExternalToolException"/> on failure — PR creation
    /// failures are NOT silently ignored, unlike pr list.
    /// </summary>
    Task<string> CreatePullRequestAsync(
        string repoSlug,
        string baseBranch,
        string headBranch,
        string title,
        string body,
        CancellationToken ct = default);
}
