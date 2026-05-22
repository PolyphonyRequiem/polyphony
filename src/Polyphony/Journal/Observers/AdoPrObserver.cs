using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Journal.Projections;
using Polyphony.Sdlc.Observers;

namespace Polyphony.Journal.Observers;

public sealed class AdoPrObserver(IAdoClient ado, RepoIdentityResolver repoIdentityResolver) : IResourceObserver
{
    private readonly IAdoClient _ado = ado;
    private readonly RepoIdentityResolver _repoIdentityResolver = repoIdentityResolver;

    public string Kind => ResourceKind.AdoPr;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expected = request.ExpectedResources.Where(resource => resource.Kind == Kind).ToArray();
        var fallbackRepo = await TryResolveFallbackRepoAsync(ct).ConfigureAwait(false);
        var expectedNumbers = new HashSet<int>();
        var observations = new List<ObservedResourceState>();

        foreach (var resource in expected)
        {
            var repo = ResolveRepo(resource, fallbackRepo);
            var pullRequestNumber = ResolvePullRequestNumber(resource);
            if (repo is null || pullRequestNumber is null)
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
            var pullRequest = await _ado.GetPullRequestAsync(repo.Organization, repo.Project, repo.Repository, pullRequestNumber.Value, ct).ConfigureAwait(false);
            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = resource.Id,
                Exists = pullRequest is not null,
                MatchesExpectedState = MatchesExpected(resource, pullRequest),
                ActualState = pullRequest?.Status ?? "missing",
                ActualAttributes = pullRequest is null
                    ? null
                    : ResourceObserverSupport.CreateActualAttributes(
                        ("organization", repo.Organization),
                        ("project", repo.Project),
                        ("repository", repo.Repository),
                        ("pr_number", pullRequestNumber.Value),
                        ("source_ref", pullRequest.SourceRefName),
                        ("target_ref", pullRequest.TargetRefName),
                        ("merge_status", pullRequest.MergeStatus),
                        ("pr_url", pullRequest.Url)),
            });
        }

        var discovered = new List<DiscoveredResourceState>();
        if (fallbackRepo is not null)
        {
            var pullRequests = await _ado.ListPullRequestsAsync(
                fallbackRepo.Organization,
                fallbackRepo.Project,
                fallbackRepo.Repository,
                AdoPullRequestStatus.Active,
                sourceBranch: null,
                ct).ConfigureAwait(false) ?? [];

            foreach (var pullRequest in pullRequests)
            {
                var branch = ResourceObserverSupport.StripRefsHeadsPrefix(pullRequest.SourceRefName);
                if (expectedNumbers.Contains(pullRequest.PullRequestId)
                    || !ResourceObserverSupport.MatchesPolyphonyBranchPattern(request.RootId, branch))
                {
                    continue;
                }

                discovered.Add(new DiscoveredResourceState
                {
                    Kind = Kind,
                    Id = pullRequest.Url,
                    MatchesPolyphonyPattern = true,
                    ActualState = pullRequest.Status,
                    ActualAttributes = ResourceObserverSupport.CreateActualAttributes(
                        ("organization", fallbackRepo.Organization),
                        ("project", fallbackRepo.Project),
                        ("repository", fallbackRepo.Repository),
                        ("pr_number", pullRequest.PullRequestId),
                        ("source_ref", pullRequest.SourceRefName),
                        ("target_ref", pullRequest.TargetRefName),
                        ("merge_status", pullRequest.MergeStatus),
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

    private async Task<RepoIdentity.AdoRepo?> TryResolveFallbackRepoAsync(CancellationToken ct)
    {
        var resolved = await _repoIdentityResolver.ResolveAsync(string.Empty, string.Empty, string.Empty, string.Empty, ct).ConfigureAwait(false);
        return resolved.Identity as RepoIdentity.AdoRepo;
    }

    private static RepoIdentity.AdoRepo? ResolveRepo(ProjectedResourceState resource, RepoIdentity.AdoRepo? fallbackRepo)
    {
        var organization = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "organization");
        var project = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "project");
        var repository = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "repository");
        if (!string.IsNullOrWhiteSpace(organization)
            && !string.IsNullOrWhiteSpace(project)
            && !string.IsNullOrWhiteSpace(repository))
        {
            return new RepoIdentity.AdoRepo(organization, project, repository);
        }

        if (ResourceObserverSupport.TryParseAdoPullRequest(resource.Id, out var fromId, out _))
        {
            return fromId;
        }

        return fallbackRepo;
    }

    private static int? ResolvePullRequestNumber(ProjectedResourceState resource)
        => ResourceObserverSupport.GetIntAttribute(resource.Attributes, "pr_number")
            ?? (ResourceObserverSupport.TryParsePullRequestNumber(resource.Id, out var fromTarget) ? fromTarget : (int?)null)
            ?? (ResourceObserverSupport.TryParseAdoPullRequest(resource.Id, out _, out var fromUrl) ? fromUrl : (int?)null);

    private static bool MatchesExpected(ProjectedResourceState resource, AdoPullRequest? pullRequest)
    {
        if (resource.Intent == ResourceIntent.EnsureAbsent)
        {
            return pullRequest is null || !string.Equals(pullRequest.Status, "active", StringComparison.OrdinalIgnoreCase);
        }

        if (pullRequest is null)
        {
            return false;
        }

        var expectedState = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "state");
        if (string.Equals(expectedState, "merged", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(pullRequest.Status, "completed", StringComparison.OrdinalIgnoreCase);
        }

        return resource.Intent != ResourceIntent.EnsurePresent
            || string.Equals(pullRequest.Status, "active", StringComparison.OrdinalIgnoreCase);
    }
}
