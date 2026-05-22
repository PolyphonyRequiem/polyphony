using Twig.Domain.Interfaces;

namespace Polyphony.Journal.Observers;

public sealed class AdoWorkItemStateObserver(IWorkItemRepository repository) : IResourceObserver
{
    private readonly IWorkItemRepository _repository = repository;

    public string Kind => ResourceKind.AdoWorkItemState;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var observations = new List<ObservedResourceState>();
        foreach (var resource in request.ExpectedResources.Where(expected => expected.Kind == Kind))
        {
            if (!ResourceObserverSupport.TryParseWorkItemId(resource.Id, out var workItemId))
            {
                observations.Add(new ObservedResourceState
                {
                    Kind = Kind,
                    Id = resource.Id,
                    Exists = false,
                    MatchesExpectedState = false,
                    ActualState = "unparseable_work_item_id",
                });
                continue;
            }

            var item = await _repository.GetByIdAsync(workItemId, ct).ConfigureAwait(false);
            var expectedState = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "target_state")
                ?? ResourceObserverSupport.GetStringAttribute(resource.Attributes, "state");
            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = resource.Id,
                Exists = item is not null,
                MatchesExpectedState = item is not null
                    && (string.IsNullOrWhiteSpace(expectedState)
                        || string.Equals(item.State, expectedState, StringComparison.OrdinalIgnoreCase)),
                ActualState = item?.State ?? "missing",
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
