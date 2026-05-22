using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Polyphony.Tagging;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Journal.Reset.Deleters;

public sealed class AdoWorkItemTagDeleter(ITwigClient twig) : IResourceDeleter
{
    private readonly ITwigClient _twig = twig;

    public string Kind => ResourceKind.AdoWorkItemTag;

    public async Task<ResourceDeleteOutcome> DeleteAsync(
        ProjectedResourceState resource,
        ObservedResourceState? observation,
        ResourceDeletionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        if (!ProjectionResetCatalog.TryGetTag(resource.Id, out var workItemId, out var expectedTag))
        {
            return new ResourceDeleteOutcome
            {
                Success = false,
                Error = $"Could not parse work-item tag target '{resource.Id}'.",
            };
        }

        await _twig.SyncAsync(ct).ConfigureAwait(false);
        var item = await _twig.ShowAsync(workItemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new ResourceDeleteOutcome
            {
                Success = false,
                Error = $"Work item {workItemId} not found in twig cache.",
            };
        }

        var raw = item["tags"]?.GetValue<string>()
            ?? item["fields"]?["System.Tags"]?.GetValue<string>();
        var tags = TagSet.Parse(raw);
        var tagsToRemove = ResolveTagsToRemove(tags, expectedTag, observation);
        if (tagsToRemove.Count == 0)
        {
            return new ResourceDeleteOutcome { Success = true, Deleted = false };
        }

        var updated = tagsToRemove.Aggregate(tags, static (current, tag) => current.Remove(tag));
        await _twig.PatchFieldsAsync(
            workItemId,
            new Dictionary<string, string> { ["System.Tags"] = updated.Format() },
            ct).ConfigureAwait(false);
        await _twig.SyncAsync(ct).ConfigureAwait(false);

        return new ResourceDeleteOutcome { Success = true, Deleted = true };
    }

    private static IReadOnlyList<string> ResolveTagsToRemove(TagSet tags, string expectedTag, ObservedResourceState? observation)
    {
        if (string.Equals(expectedTag, PolyphonyTags.Planned, StringComparison.Ordinal))
        {
            return tags.Contains(expectedTag) ? [expectedTag] : [];
        }

        if (expectedTag.StartsWith(PolyphonyTags.FacetsPrefix + "=", StringComparison.Ordinal))
        {
            return [.. tags.Where(tag => tag.StartsWith(PolyphonyTags.FacetsPrefix + "=", StringComparison.Ordinal))];
        }

        if (expectedTag.StartsWith(PolyphonyTags.RunStartedAtPrefix + "=", StringComparison.Ordinal))
        {
            return [.. tags.Where(tag => tag.StartsWith(PolyphonyTags.RunStartedAtPrefix + "=", StringComparison.Ordinal))];
        }

        if (tags.Contains(expectedTag))
        {
            return [expectedTag];
        }

        return observation?.ActualState is { Length: > 0 } actualTag && tags.Contains(actualTag)
            ? [actualTag]
            : [];
    }
}
