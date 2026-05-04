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
