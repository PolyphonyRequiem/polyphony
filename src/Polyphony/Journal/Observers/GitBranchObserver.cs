using Polyphony.Infrastructure.Processes;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Observers;

public sealed class GitBranchObserver(IGitClient git) : IResourceObserver
{
    private readonly IGitClient _git = git;

    public string Kind => ResourceKind.GitBranch;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expected = request.ExpectedResources.Where(resource => resource.Kind == Kind).ToArray();
        var localBranches = await _git.ListLocalBranchesAsync("*", ct).ConfigureAwait(false);
        var remoteBranches = await _git.ListRemoteBranchesAsync(ct).ConfigureAwait(false);
        var allBranches = localBranches
            .Concat(remoteBranches)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(branch => branch, StringComparer.Ordinal)
            .ToArray();

        var expectedIds = expected.Select(resource => resource.Id).ToHashSet(StringComparer.Ordinal);
        var headCache = new Dictionary<string, (string? Local, string? Remote)>(StringComparer.Ordinal);

        var observations = new List<ObservedResourceState>();
        foreach (var resource in expected)
        {
            var heads = await GetHeadsAsync(resource.Id, headCache, ct).ConfigureAwait(false);
            var exists = heads.Local is not null || heads.Remote is not null;
            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = resource.Id,
                Exists = exists,
                MatchesExpectedState = MatchesExpected(resource, exists, heads.Local, heads.Remote),
                ActualState = FormatActualState(exists, heads.Local, heads.Remote),
                ActualAttributes = exists
                    ? ResourceObserverSupport.CreateActualAttributes(("local_sha", heads.Local), ("remote_sha", heads.Remote))
                    : null,
            });
        }

        var discovered = new List<DiscoveredResourceState>();
        foreach (var branch in allBranches)
        {
            if (expectedIds.Contains(branch) || !ResourceObserverSupport.MatchesPolyphonyBranchPattern(request.RootId, branch))
            {
                continue;
            }

            var heads = await GetHeadsAsync(branch, headCache, ct).ConfigureAwait(false);
            discovered.Add(new DiscoveredResourceState
            {
                Kind = Kind,
                Id = branch,
                MatchesPolyphonyPattern = true,
                ActualState = FormatActualState(true, heads.Local, heads.Remote),
                ActualAttributes = ResourceObserverSupport.CreateActualAttributes(("local_sha", heads.Local), ("remote_sha", heads.Remote)),
            });
        }

        return new ResourceObservationBatch
        {
            Kind = Kind,
            Observations = observations.OrderBy(observation => observation.Id, StringComparer.Ordinal).ToArray(),
            DiscoveredResources = discovered.OrderBy(resource => resource.Id, StringComparer.Ordinal).ToArray(),
        };
    }

    private async Task<(string? Local, string? Remote)> GetHeadsAsync(
        string branch,
        IDictionary<string, (string? Local, string? Remote)> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(branch, out var heads))
        {
            return heads;
        }

        var local = await _git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);
        var remote = await GetRemoteHeadAsync(branch, ct).ConfigureAwait(false);
        heads = (local, remote);
        cache[branch] = heads;
        return heads;
    }

    private async Task<string?> GetRemoteHeadAsync(string branch, CancellationToken ct)
    {
        var heads = await _git.LsRemoteHeadsAsync("origin", $"refs/heads/{branch}", ct).ConfigureAwait(false);
        if (heads.Count == 0)
        {
            return null;
        }

        var first = heads[0].Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return first.Length > 0 ? first[0] : null;
    }

    private static bool MatchesExpected(ProjectedResourceState expected, bool exists, string? localSha, string? remoteSha)
    {
        if (expected.Intent == ResourceIntent.EnsureAbsent)
        {
            return !exists;
        }

        if (!exists)
        {
            return false;
        }

        var expectedSha = ResourceObserverSupport.GetBranchExpectedSha(expected.Attributes);
        return string.IsNullOrWhiteSpace(expectedSha)
            || string.Equals(localSha, expectedSha, StringComparison.Ordinal)
            || string.Equals(remoteSha, expectedSha, StringComparison.Ordinal);
    }

    private static string FormatActualState(bool exists, string? localSha, string? remoteSha)
    {
        if (!exists)
        {
            return "missing";
        }

        return (localSha, remoteSha) switch
        {
            ({ Length: > 0 } local, { Length: > 0 } remote) when string.Equals(local, remote, StringComparison.Ordinal) => local,
            ({ Length: > 0 } local, { Length: > 0 } remote) => $"local:{local};remote:{remote}",
            ({ Length: > 0 } local, _) => local,
            (_, { Length: > 0 } remote) => remote,
            _ => "present",
        };
    }
}
