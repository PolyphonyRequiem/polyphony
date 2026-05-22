using System.Security.Cryptography;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Observers;

public sealed class PlanFileObserver : IResourceObserver
{
    public string Kind => ResourceKind.PlanFile;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expected = request.ExpectedResources.Where(resource => resource.Kind == Kind).ToArray();
        var observations = new List<ObservedResourceState>();
        foreach (var resource in expected)
        {
            var path = Path.GetFullPath(resource.Id);
            var exists = File.Exists(path);
            var actualHash = exists ? await ComputeShaAsync(path, ct).ConfigureAwait(false) : null;
            var expectedHash = ResourceObserverSupport.GetStringAttribute(resource.Attributes, "content_sha256")
                ?? ResourceObserverSupport.GetStringAttribute(resource.Attributes, "children_sha256");
            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = resource.Id,
                Exists = exists,
                MatchesExpectedState = resource.Intent == ResourceIntent.EnsureAbsent
                    ? !exists
                    : exists && (string.IsNullOrWhiteSpace(expectedHash)
                        || string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)),
                ActualState = exists ? actualHash ?? "present" : "missing",
                ActualAttributes = exists
                    ? ResourceObserverSupport.CreateActualAttributes(("path", path))
                    : null,
            });
        }

        return new ResourceObservationBatch
        {
            Kind = Kind,
            Observations = observations.OrderBy(observation => observation.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            DiscoveredResources = [],
        };
    }

    private static async Task<string> ComputeShaAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
