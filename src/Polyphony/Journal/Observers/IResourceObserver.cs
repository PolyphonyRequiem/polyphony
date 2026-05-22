using System.Text.Json.Nodes;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Observers;

public interface IResourceObserver
{
    string Kind { get; }
    bool CanObserve { get; }
    string? DeferredReason { get; }

    Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct);
}

public sealed record ResourceObservationRequest
{
    public required int RootId { get; init; }
    public required ProjectedResourceState[] ExpectedResources { get; init; }
}

public sealed record ObservedResourceState
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required bool Exists { get; init; }
    public required bool MatchesExpectedState { get; init; }
    public string? ActualState { get; init; }
    public JsonObject? ActualAttributes { get; init; }
}

public sealed record DiscoveredResourceState
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required bool MatchesPolyphonyPattern { get; init; }
    public string? ActualState { get; init; }
    public JsonObject? ActualAttributes { get; init; }
}

public sealed record ResourceObservationBatch
{
    public required string Kind { get; init; }
    public required ObservedResourceState[] Observations { get; init; }
    public required DiscoveredResourceState[] DiscoveredResources { get; init; }
}
