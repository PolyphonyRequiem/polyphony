using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Routing;

namespace Polyphony.Commands;

/// <summary>
/// Outputs the work item hierarchy with role annotations.
/// </summary>
public sealed class HierarchyCommand(HierarchyWalker walker)
{
    /// <summary>
    /// Output the work item hierarchy with role annotations.
    /// </summary>
    /// <param name="workItem">ADO work item ID (root of hierarchy to display)</param>
    /// <param name="depth">Maximum depth to traverse</param>
    /// <param name="config">Path to .conductor/process-config.yaml</param>
    [Command("hierarchy")]
    public async Task<int> Hierarchy(int workItem, int depth = 3, string config = ".conductor/process-config.yaml", CancellationToken ct = default)
    {
        var result = await walker.WalkAsync(workItem, depth, ct);

        if (result is null)
        {
            Console.WriteLine($$"""{"error":"Work item {{workItem}} not found","work_item_id":{{workItem}}}""");
            return ExitCodes.CacheError;
        }

        var normalized = EnsureChildrenArrays(result);
        Console.WriteLine(JsonSerializer.Serialize(normalized, PolyphonyJsonContext.Default.HierarchyResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Recursively replaces null Children with empty arrays so the JSON output
    /// always contains a "children" field per node.
    /// </summary>
    private static HierarchyResult EnsureChildrenArrays(HierarchyResult node)
    {
        var children = node.Children ?? [];
        var normalized = new HierarchyResult[children.Length];
        for (var i = 0; i < children.Length; i++)
        {
            normalized[i] = EnsureChildrenArrays(children[i]);
        }

        return node with { Children = normalized };
    }
}
