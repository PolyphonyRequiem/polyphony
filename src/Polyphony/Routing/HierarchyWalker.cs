using Polyphony.Configuration;
using Twig.Domain.Interfaces;

namespace Polyphony.Routing;

/// <summary>
/// Recursively traverses the work item tree to a specified depth,
/// annotating each node with capabilities from the process config.
/// </summary>
public sealed class HierarchyWalker(ProcessConfig processConfig, IWorkItemRepository repository)
{
    /// <summary>
    /// Walks the hierarchy starting from <paramref name="rootId"/> down to <paramref name="maxDepth"/> levels.
    /// Each node is annotated with capabilities from <see cref="ProcessConfig.Types"/>.
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
        var capabilities = LookupCapabilities(typeName);
        item.Fields.TryGetValue("System.Tags", out var tags);

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
            Capabilities = capabilities,
            State = item.State,
            Tags = tags,
            Children = children,
        };
    }

    private string[] LookupCapabilities(string typeName)
    {
        if (processConfig.Types.TryGetValue(typeName, out var typeConfig))
            return typeConfig.Capabilities;

        return [];
    }
}
