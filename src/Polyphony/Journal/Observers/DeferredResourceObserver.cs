namespace Polyphony.Journal.Observers;

public sealed class DeferredResourceObserver(string kind, string deferredReason) : IResourceObserver
{
    public string Kind { get; } = kind;
    public bool CanObserve => false;
    public string? DeferredReason { get; } = deferredReason;

    public Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(new ResourceObservationBatch
        {
            Kind = Kind,
            Observations = [],
            DiscoveredResources = [],
        });
    }
}
