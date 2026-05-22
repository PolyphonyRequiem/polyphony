using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Observers;

public sealed class LockFileObserver : IResourceObserver
{
    public string Kind => ResourceKind.LockFile;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var observations = request.ExpectedResources
            .Where(resource => resource.Kind == Kind)
            .Select(resource =>
            {
                var path = Path.GetFullPath(resource.Id);
                var exists = File.Exists(path);
                return new ObservedResourceState
                {
                    Kind = Kind,
                    Id = resource.Id,
                    Exists = exists,
                    MatchesExpectedState = resource.Intent == ResourceIntent.EnsureAbsent ? !exists : exists,
                    ActualState = exists ? "present" : "missing",
                    ActualAttributes = ResourceObserverSupport.CreateActualAttributes(("path", path)),
                };
            })
            .OrderBy(observation => observation.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(new ResourceObservationBatch
        {
            Kind = Kind,
            Observations = observations,
            DiscoveredResources = [],
        });
    }
}
