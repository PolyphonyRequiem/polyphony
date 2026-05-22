using Polyphony.Infrastructure.Paths;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Observers;

public sealed class ManifestFileObserver(PolyphonyStatePaths statePaths) : IResourceObserver
{
    private readonly PolyphonyStatePaths _statePaths = statePaths;

    public string Kind => ResourceKind.ManifestFile;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var observations = new List<ObservedResourceState>();
        foreach (var resource in request.ExpectedResources.Where(resource => resource.Kind == Kind))
        {
            var path = await ResolvePathAsync(resource, ct).ConfigureAwait(false);
            var exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = resource.Id,
                Exists = exists,
                MatchesExpectedState = resource.Intent == ResourceIntent.EnsureAbsent ? !exists : exists,
                ActualState = exists ? "present" : "missing",
                ActualAttributes = !string.IsNullOrWhiteSpace(path)
                    ? ResourceObserverSupport.CreateActualAttributes(("path", path))
                    : null,
            });
        }

        return new ResourceObservationBatch
        {
            Kind = Kind,
            Observations = observations.OrderBy(observation => observation.Id, StringComparer.Ordinal).ToArray(),
            DiscoveredResources = [],
        };
    }

    private async Task<string?> ResolvePathAsync(ProjectedResourceState resource, CancellationToken ct)
    {
        if (!resource.Id.StartsWith("manifest:", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(resource.Id);
        }

        return int.TryParse(resource.Id["manifest:".Length..], out var rootId)
            ? await _statePaths.GetManifestPathAsync(rootId, ct).ConfigureAwait(false)
            : null;
    }
}
