using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Cross-platform pull-request operations dispatched on a resolved
/// <see cref="RepoIdentity"/>. Wraps <see cref="IGhClient"/> and
/// <see cref="IAdoClient"/> with a single neutral surface so the
/// PlanCommands / PrCommands / BranchCommands consumers don't have
/// to re-implement the same variant-branching switch in every verb.
///
/// <para><b>Field-shape parity</b> is provided by
/// <see cref="GhPullRequestPollAdapter"/>: ADO poll data is projected
/// into the GitHub poll-data shape so downstream consumers can keep
/// reading <see cref="GhPullRequestPollData"/> regardless of platform.
/// Lossy fields (Comments, AuthorLogin) are documented on the adapter.</para>
///
/// <para><b>Failure mode</b>: when a platform-specific branch is hit on
/// an unsupported identity (e.g. an ADO identity reaches a verb whose
/// AdoClient was not registered), the helper throws
/// <see cref="InvalidOperationException"/>. Verbs surface this through
/// their normal routing-style envelope.</para>
/// </summary>
public sealed class PullRequestReader(IGhClient gh, IAdoClient? ado)
{
    /// <summary>List PRs filtered by source (head) branch + state. Pass <c>null</c> headBranch to skip the filter.</summary>
    /// <param name="state">"open" | "merged" | "closed" | "all" — gh's vocabulary; mapped to ADO equivalents internally.</param>
    /// <param name="limit">Optional cap; passed through to gh, ignored by ADO (which has no list-limit knob).</param>
    public async Task<IReadOnlyList<PullRequestSummary>> ListByHeadAsync(
        RepoIdentity identity, string? headBranch, string state, int? limit, CancellationToken ct)
    {
        switch (identity)
        {
            case RepoIdentity.GitHubRepo githubRepo:
                return await gh.ListPullRequestsAsync(
                    githubRepo.Slug,
                    new PrListFilters(Head: string.IsNullOrEmpty(headBranch) ? null : headBranch, State: state, Limit: limit),
                    ct).ConfigureAwait(false);
            case RepoIdentity.AdoRepo adoRepo:
                if (ado is null) throw new InvalidOperationException(
                    "IAdoClient is not configured but RepoIdentity resolved to AdoRepo");
                var status = MapState(state);
                var sourceBranch = string.IsNullOrEmpty(headBranch) ? null : $"refs/heads/{headBranch}";
                var raw = await ado.ListPullRequestsAsync(
                    adoRepo.Organization, adoRepo.Project, adoRepo.Repository,
                    status, sourceBranch, ct).ConfigureAwait(false);
                return raw is null
                    ? Array.Empty<PullRequestSummary>()
                    : [.. raw.Select(p => new PullRequestSummary(
                        Number: p.PullRequestId,
                        HeadRefName: StripRefsHeadsPrefix(p.SourceRefName),
                        Url: p.Url,
                        MergedAt: null))];
            default:
                throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}");
        }
    }

    /// <summary>Fetch poll-data snapshot. ADO data is projected through <see cref="GhPullRequestPollAdapter"/>.</summary>
    public async Task<GhPullRequestPollData?> GetPollDataAsync(
        RepoIdentity identity, int prNumber, CancellationToken ct)
    {
        switch (identity)
        {
            case RepoIdentity.GitHubRepo githubRepo:
                return await gh.GetPullRequestPollDataAsync(githubRepo.Slug, prNumber, ct).ConfigureAwait(false);
            case RepoIdentity.AdoRepo adoRepo:
                if (ado is null) throw new InvalidOperationException(
                    "IAdoClient is not configured but RepoIdentity resolved to AdoRepo");
                var poll = await ado.GetPullRequestPollDataAsync(
                    adoRepo.Organization, adoRepo.Project, adoRepo.Repository, prNumber, ct).ConfigureAwait(false);
                return poll is null ? null : GhPullRequestPollAdapter.FromAdo(poll);
            default:
                throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}");
        }
    }

    /// <summary>Edit a PR body. Returns true when the platform reported success.</summary>
    public async Task<bool> EditBodyAsync(
        RepoIdentity identity, int prNumber, string body, CancellationToken ct)
    {
        switch (identity)
        {
            case RepoIdentity.GitHubRepo githubRepo:
                return await gh.EditPullRequestBodyAsync(githubRepo.Slug, prNumber, body, ct).ConfigureAwait(false);
            case RepoIdentity.AdoRepo adoRepo:
                if (ado is null) throw new InvalidOperationException(
                    "IAdoClient is not configured but RepoIdentity resolved to AdoRepo");
                return await ado.EditPullRequestBodyAsync(
                    adoRepo.Organization, adoRepo.Project, adoRepo.Repository, prNumber, body, ct).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}");
        }
    }

    /// <summary>Post a non-review comment on a PR.</summary>
    public async Task<bool> CommentAsync(
        RepoIdentity identity, int prNumber, string body, CancellationToken ct)
    {
        switch (identity)
        {
            case RepoIdentity.GitHubRepo githubRepo:
                return await gh.CommentPullRequestAsync(githubRepo.Slug, prNumber, body, ct).ConfigureAwait(false);
            case RepoIdentity.AdoRepo adoRepo:
                if (ado is null) throw new InvalidOperationException(
                    "IAdoClient is not configured but RepoIdentity resolved to AdoRepo");
                var thread = await ado.CreatePullRequestCommentThreadAsync(
                    adoRepo.Organization, adoRepo.Project, adoRepo.Repository, prNumber, body, ct).ConfigureAwait(false);
                return thread is not null;
            default:
                throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}");
        }
    }

    /// <summary>Close (gh) / abandon (ado) a PR with an optional pre-close comment.</summary>
    public async Task<bool> CloseAsync(
        RepoIdentity identity, int prNumber, string commentBeforeClose, CancellationToken ct)
    {
        switch (identity)
        {
            case RepoIdentity.GitHubRepo githubRepo:
                return await gh.ClosePullRequestAsync(githubRepo.Slug, prNumber, commentBeforeClose, ct).ConfigureAwait(false);
            case RepoIdentity.AdoRepo adoRepo:
                if (ado is null) throw new InvalidOperationException(
                    "IAdoClient is not configured but RepoIdentity resolved to AdoRepo");
                return await ado.ClosePullRequestAsync(
                    adoRepo.Organization, adoRepo.Project, adoRepo.Repository, prNumber, commentBeforeClose, ct).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}");
        }
    }

    /// <summary>Create a PR and return its URL (or null when the platform call returned no URL).</summary>
    public async Task<string?> CreateAsync(
        RepoIdentity identity, string baseBranch, string headBranch, string title, string body, CancellationToken ct)
    {
        switch (identity)
        {
            case RepoIdentity.GitHubRepo githubRepo:
            {
                var url = await gh.CreatePullRequestAsync(githubRepo.Slug, baseBranch, headBranch, title, body, ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(url) ? null : url;
            }
            case RepoIdentity.AdoRepo adoRepo:
                if (ado is null) throw new InvalidOperationException(
                    "IAdoClient is not configured but RepoIdentity resolved to AdoRepo");
                var pr = await ado.CreatePullRequestAsync(
                    adoRepo.Organization, adoRepo.Project, adoRepo.Repository,
                    sourceBranch: headBranch, targetBranch: baseBranch,
                    title: title, description: body, ct).ConfigureAwait(false);
                return pr?.Url;
            default:
                throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}");
        }
    }

    /// <summary>Fetch the list of changed files for a PR, projected to the GH-shape.</summary>
    /// <returns>Null when the platform reports the PR is missing (HTTP 404 / null payload).</returns>
    public async Task<IReadOnlyList<GhPullRequestChangedFile>?> GetChangedFilesAsync(
        RepoIdentity identity, int prNumber, CancellationToken ct)
    {
        switch (identity)
        {
            case RepoIdentity.GitHubRepo githubRepo:
                return await gh.GetPullRequestFilesAsync(githubRepo.Slug, prNumber, ct).ConfigureAwait(false);
            case RepoIdentity.AdoRepo adoRepo:
                if (ado is null) throw new InvalidOperationException(
                    "IAdoClient is not configured but RepoIdentity resolved to AdoRepo");
                IReadOnlyList<AdoPullRequestChangedFile>? files;
                try
                {
                    files = await ado.GetPullRequestFilesAsync(
                        adoRepo.Organization, adoRepo.Project, adoRepo.Repository, prNumber, ct).ConfigureAwait(false);
                }
                catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }
                return files is null
                    ? null
                    : [.. files.Select(f => new GhPullRequestChangedFile(f.Path, f.Additions, f.Deletions))];
            default:
                throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}");
        }
    }

    /// <summary>Build the canonical web URL for a PR on the resolved platform.</summary>
    public static string BuildPrUrl(RepoIdentity identity, int prNumber)
        => identity switch
        {
            RepoIdentity.GitHubRepo gh => $"https://github.com/{gh.Slug}/pull/{prNumber}",
            RepoIdentity.AdoRepo ado => $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_git/{ado.Repository}/pullrequest/{prNumber}",
            _ => throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}"),
        };

    /// <summary>Build the platform-neutral repo "slug" string for diagnostics + envelopes.</summary>
    public static string BuildRepoSlug(RepoIdentity identity)
        => identity switch
        {
            RepoIdentity.GitHubRepo gh => gh.Slug,
            RepoIdentity.AdoRepo ado => $"{ado.Organization}/{ado.Project}/{ado.Repository}",
            _ => throw new InvalidOperationException($"Unhandled RepoIdentity variant: {identity.GetType().Name}"),
        };

    private static AdoPullRequestStatus MapState(string state)
        => state.ToLowerInvariant() switch
        {
            "open" => AdoPullRequestStatus.Active,
            "merged" => AdoPullRequestStatus.Completed,
            "closed" => AdoPullRequestStatus.Abandoned,
            "all" or "" => AdoPullRequestStatus.All,
            _ => AdoPullRequestStatus.All,
        };

    private static string StripRefsHeadsPrefix(string refName)
    {
        const string prefix = "refs/heads/";
        return refName.StartsWith(prefix, StringComparison.Ordinal)
            ? refName[prefix.Length..]
            : refName;
    }
}

