using Polyphony.Infrastructure.Processes;
using Polyphony.Journal.Projections;
using Polyphony.Sdlc.Observers;

namespace Polyphony.Journal.Observers;

public sealed class GitHubPrObserver(IGhClient gh, RepoIdentityResolver repoIdentityResolver) : IResourceObserver
{
    private readonly IGhClient _gh = gh;
    private readonly RepoIdentityResolver _repoIdentityResolver = repoIdentityResolver;

    public string Kind => ResourceKind.GitHubPr;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expected = request.ExpectedResources.Where(resource => resource.Kind == Kind).ToArray();
        var fallbackSlug = await TryResolveFallbackSlugAsync(ct).ConfigureAwait(false);
        var expectedNumbers = new HashSet<int>();
        var observations = new List<ObservedResourceState>();

        foreach (var resource in expected)
        {
            var slug = ResolveSlug(resource, fallbackSlug);
            var pullRequestNumber = ResolvePullRequestNumber(resource);
            if (string.IsNullOrWhiteSpace(slug) || pullRequestNumber is null)
            {
                observations.Add(new ObservedResourceState
                {
                    Kind = Kind,
                    Id = resource.Id,
                    Exists = false,
                    MatchesExpectedState = false,
                    ActualState = "unresolvable_pr",
                });
                continue;
            }

            expectedNumbers.Add(pullRequestNumber.Value);
            var state = await _gh.GetPullRequestStateAsync(slug, pullRequestNumber.Value, ct).ConfigureAwait(false);
            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = resource.Id,
                Exists = state is not null,
                MatchesExpectedState = MatchesExpected(resource, state),
                ActualState = state?.State ?? "missing",
                ActualAttributes = state is null
                    ? null
                    : ResourceObserverSupport.CreateActualAttributes(
                        ("repo_slug", slug),
                        ("pr_number", pullRequestNumber.Value),
                        ("head_branch", state.HeadRefName),
                        ("head_sha", state.HeadRefOid),
                        ("merge_sha", state.MergeCommitSha)),
            });
        }

        var discovered = new List<DiscoveredResourceState>();
        if (!string.IsNullOrWhiteSpace(fallbackSlug))
        {
            var pullRequests = await _gh.ListPullRequestsAsync(fallbackSlug, new PrListFilters(State: "open", Limit: 200), ct).ConfigureAwait(false);
            foreach (var pullRequest in pullRequests)
            {
                if (expectedNumbers.Contains(pullRequest.Number)
                    || !ResourceObserverSupport.MatchesPolyphonyBranchPattern(request.RootId, pullRequest.HeadRefName))
                {
                    continue;
                }

                discovered.Add(new DiscoveredResourceState
                {
                    Kind = Kind,
                    Id = pullRequest.Url ?? $"pr#{pullRequest.Number}",
                    MatchesPolyphonyPattern = true,
                    ActualState = "OPEN",
                    ActualAttributes = ResourceObserverSupport.CreateActualAttributes(
                        ("repo_slug", fallbackSlug),
                        ("pr_number", pullRequest.Number),
                        ("head_branch", pullRequest.HeadRefName),
                        ("pr_url", pullRequest.Url)),
                });
            }
        }

        return new ResourceObservationBatch
        {
            Kind = Kind,
            Observations = observations.OrderBy(observation => observation.Id, StringComparer.Ordinal).ToArray(),
            DiscoveredResources = discovered.OrderBy(resource => resource.Id, StringComparer.Ordinal).ToArray(),
        };
    }

    private async Task<string?> TryResolveFallbackSlugAsync(CancellationToken ct)
    {
        var resolved = await _repoIdentityResolver.ResolveAsync(string.Empty, string.Empty, string.Empty, string.Empty, ct).ConfigureAwait(false);
        return resolved.Identity is RepoIdentity.GitHubRepo githubRepo
            ? githubRepo.Slug
            : null;
    }

    private static string? ResolveSlug(ProjectedResourceState resource, string? fallbackSlug)
    {
        var repoSlug = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "repo_slug");
        if (!string.IsNullOrWhiteSpace(repoSlug) && repoSlug.Count(character => character == '/') == 1)
        {
            return repoSlug;
        }

        if (ResourceObserverSupport.TryParseGitHubPullRequest(resource.Id, out var slug, out _))
        {
            return slug;
        }

        return fallbackSlug;
    }

    private static int? ResolvePullRequestNumber(ProjectedResourceState resource)
        => ResourceObserverSupport.GetIntAttribute(resource.Attributes, "pr_number")
            ?? (ResourceObserverSupport.TryParsePullRequestNumber(resource.Id, out var fromTarget) ? fromTarget : (int?)null)
            ?? (ResourceObserverSupport.TryParseGitHubPullRequest(resource.Id, out _, out var fromUrl) ? fromUrl : (int?)null);

    private static bool MatchesExpected(ProjectedResourceState resource, GhPullRequestState? state)
    {
        if (resource.Intent == ResourceIntent.EnsureAbsent)
        {
            return state is null || !string.Equals(state.State, "OPEN", StringComparison.OrdinalIgnoreCase);
        }

        if (state is null)
        {
            return false;
        }

        var expectedState = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "state");
        if (!string.IsNullOrWhiteSpace(expectedState))
        {
            return string.Equals(state.State, expectedState, StringComparison.OrdinalIgnoreCase);
        }

        return resource.Intent != ResourceIntent.EnsurePresent
            || string.Equals(state.State, "OPEN", StringComparison.OrdinalIgnoreCase);
    }
}
