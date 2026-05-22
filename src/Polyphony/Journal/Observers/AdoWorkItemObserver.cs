using Polyphony.Journal.Projections;
using Twig.Domain.Interfaces;

namespace Polyphony.Journal.Observers;

public sealed class AdoWorkItemObserver(IWorkItemRepository repository) : IResourceObserver
{
    private readonly IWorkItemRepository _repository = repository;

    public string Kind => ResourceKind.AdoWorkItem;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var observations = new List<ObservedResourceState>();
        foreach (var expected in request.ExpectedResources.Where(resource => resource.Kind == Kind))
        {
            if (!ResourceObserverSupport.TryParseWorkItemId(expected.Id, out var workItemId))
            {
                observations.Add(new ObservedResourceState
                {
                    Kind = Kind,
                    Id = expected.Id,
                    Exists = false,
                    MatchesExpectedState = false,
                    ActualState = "unparseable_work_item_id",
                });
                continue;
            }

            var item = await _repository.GetByIdAsync(workItemId, ct).ConfigureAwait(false);
            var exists = item is not null;
            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = expected.Id,
                Exists = exists,
                MatchesExpectedState = expected.Intent == ResourceIntent.EnsureAbsent ? !exists : exists,
                ActualState = exists ? "present" : "missing",
            });
        }

        return new ResourceObservationBatch
        {
            Kind = Kind,
            Observations = observations.OrderBy(observation => observation.Id, StringComparer.Ordinal).ToArray(),
            DiscoveredResources = [],
        };
    }
}
