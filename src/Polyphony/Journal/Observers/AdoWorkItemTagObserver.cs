using Polyphony.Tagging;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Polyphony.Journal.Observers;

public sealed class AdoWorkItemTagObserver(IWorkItemRepository repository) : IResourceObserver
{
    private readonly IWorkItemRepository _repository = repository;

    public string Kind => ResourceKind.AdoWorkItemTag;
    public bool CanObserve => true;
    public string? DeferredReason => null;

    public async Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expected = request.ExpectedResources.Where(resource => resource.Kind == Kind).ToArray();
        var observations = new List<ObservedResourceState>();
        foreach (var resource in expected)
        {
            if (!ResourceObserverSupport.TryParseWorkItemTagId(resource.Id, out var workItemId, out var tag))
            {
                observations.Add(new ObservedResourceState
                {
                    Kind = Kind,
                    Id = resource.Id,
                    Exists = false,
                    MatchesExpectedState = false,
                    ActualState = "unparseable_tag_id",
                });
                continue;
            }

            var item = await _repository.GetByIdAsync(workItemId, ct).ConfigureAwait(false);
            var tagSet = item is null ? null : ReadTags(item);
            var exists = tagSet?.Contains(tag) == true;
            var matches = resource.Intent switch
            {
                ResourceIntent.EnsureAbsent => !exists,
                _ => exists,
            };

            observations.Add(new ObservedResourceState
            {
                Kind = Kind,
                Id = resource.Id,
                Exists = exists,
                MatchesExpectedState = matches,
                ActualState = exists ? tag : "missing",
                ActualAttributes = tagSet is null
                    ? null
                    : ResourceObserverSupport.CreateActualAttributes(("tags", string.Join(';', tagSet))),
            });
        }

        var discovered = new List<DiscoveredResourceState>();
        var expectedIds = expected.Select(resource => resource.Id).ToHashSet(StringComparer.Ordinal);
        var subtree = await LoadSubtreeAsync(request.RootId, ct).ConfigureAwait(false);
        foreach (var item in subtree)
        {
            foreach (var tag in ReadTags(item).Where(IsPolyphonyTag))
            {
                var id = $"{item.Id}:{tag}";
                if (expectedIds.Contains(id))
                {
                    continue;
                }

                discovered.Add(new DiscoveredResourceState
                {
                    Kind = Kind,
                    Id = id,
                    MatchesPolyphonyPattern = true,
                    ActualState = tag,
                    ActualAttributes = ResourceObserverSupport.CreateActualAttributes(("work_item_id", item.Id), ("tags", item.Fields.TryGetValue("System.Tags", out var raw) ? raw : null)),
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

    private async Task<IReadOnlyList<WorkItem>> LoadSubtreeAsync(int rootId, CancellationToken ct)
    {
        var root = await _repository.GetByIdAsync(rootId, ct).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        var results = new List<WorkItem>();
        await AddRecursiveAsync(root, results, ct).ConfigureAwait(false);
        return results;
    }

    private async Task AddRecursiveAsync(WorkItem item, ICollection<WorkItem> results, CancellationToken ct)
    {
        results.Add(item);
        var children = await _repository.GetChildrenAsync(item.Id, ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            await AddRecursiveAsync(child, results, ct).ConfigureAwait(false);
        }
    }

    private static TagSet ReadTags(WorkItem item)
        => item.Fields.TryGetValue("System.Tags", out var raw)
            ? TagSet.Parse(raw)
            : TagSet.Parse(string.Empty);

    private static bool IsPolyphonyTag(string tag)
        => string.Equals(tag, PolyphonyTags.InScope, StringComparison.Ordinal)
            || tag.StartsWith("polyphony:", StringComparison.Ordinal);
}
