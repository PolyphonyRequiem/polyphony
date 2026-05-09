using Polyphony.Configuration;
using Polyphony.Sdlc;
using Polyphony.Tagging;
using Twig.Domain.Interfaces;

namespace Polyphony.Routing;

/// <summary>
/// Recursively traverses the work item tree to a specified depth,
/// annotating each node with facets from the process config — or, when an
/// item carries a <c>polyphony:facets=&lt;csv&gt;</c> tag, the per-item
/// override (per F2 / architect <c>apex_facets</c> declaration in plan
/// front-matter; round-tripped via <see cref="FacetTagParser"/>).
/// </summary>
public sealed class HierarchyWalker(ProcessConfig processConfig, IWorkItemRepository repository)
{
    /// <summary>
    /// Walks the hierarchy starting from <paramref name="rootId"/> down to <paramref name="maxDepth"/> levels.
    /// Each node is annotated with facets from <see cref="ProcessConfig.Types"/>,
    /// overridden when the item carries a <c>polyphony:facets=&lt;csv&gt;</c> tag.
    /// </summary>
    /// <param name="rootId">The work item ID to start from.</param>
    /// <param name="maxDepth">Maximum depth to traverse. 0 returns only the root node.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="HierarchyResult"/> tree, or null if the root item is not found.</returns>
    public async Task<HierarchyResult?> WalkAsync(int rootId, int maxDepth, CancellationToken ct)
    {
        var root = await repository.GetByIdAsync(rootId, ct);
        if (root is null)
            return null;

        return await BuildNodeAsync(root, maxDepth, currentDepth: 0, ct);
    }

    private async Task<HierarchyResult> BuildNodeAsync(
        Twig.Domain.Aggregates.WorkItem item,
        int maxDepth,
        int currentDepth,
        CancellationToken ct)
    {
        var typeName = item.Type.Value;
        item.Fields.TryGetValue("System.Tags", out var tags);
        var facets = ResolveEffectiveFacets(item.Id, typeName, tags);

        HierarchyResult[]? children = null;

        if (currentDepth < maxDepth)
        {
            var childItems = await repository.GetChildrenAsync(item.Id, ct);

            if (childItems.Count > 0)
            {
                var childResults = new HierarchyResult[childItems.Count];
                for (var i = 0; i < childItems.Count; i++)
                {
                    childResults[i] = await BuildNodeAsync(childItems[i], maxDepth, currentDepth + 1, ct);
                }

                children = childResults;
            }
        }

        return new HierarchyResult
        {
            WorkItemId = item.Id,
            Title = item.Title,
            Type = typeName,
            Facets = facets,
            State = item.State,
            Tags = tags,
            Children = children,
        };
    }

    private string[] LookupFacets(string typeName)
    {
        if (processConfig.Types.TryGetValue(typeName, out var typeConfig))
            return typeConfig.Facets;

        return [];
    }

    /// <summary>
    /// Resolves the effective facet set for an item: the
    /// <c>polyphony:facets=&lt;csv&gt;</c> tag override when present and
    /// non-empty, otherwise the type-config defaults from
    /// <see cref="LookupFacets"/>. Throws on a malformed override (consistent
    /// with <c>state next-ready</c>) so a typo surfaces immediately rather
    /// than silently degrading to type defaults.
    /// </summary>
    private string[] ResolveEffectiveFacets(int itemId, string typeName, string? tagsRaw)
    {
        var defaults = LookupFacets(typeName);
        if (string.IsNullOrWhiteSpace(tagsRaw)) return defaults;

        var tagSet = TagSet.Parse(tagsRaw);
        var parsed = FacetTagParser.TryExtract(tagSet);
        if (parsed is null) return defaults;
        if (!parsed.IsValid)
        {
            throw new InvalidOperationException(
                $"Item {itemId} has malformed polyphony:facets tag — unknown facet(s): {string.Join(", ", parsed.UnknownFacets)}.");
        }
        return parsed.Facets.Count == 0 ? defaults : parsed.Facets.ToArray();
    }
}


