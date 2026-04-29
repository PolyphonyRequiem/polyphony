using System.Text.Json;
using ConsoleAppFramework;

namespace Polyphony.Commands;

/// <summary>
/// Outputs the work item hierarchy with role annotations.
/// </summary>
public sealed class HierarchyCommand
{
    /// <summary>
    /// Output the work item hierarchy with role annotations.
    /// </summary>
    /// <param name="workItem">ADO work item ID (root of hierarchy to display)</param>
    /// <param name="depth">Maximum depth to traverse</param>
    /// <param name="config">Path to .conductor/process-config.yaml</param>
    [Command("hierarchy")]
    public int Hierarchy(int workItem, int depth = 3, string config = ".conductor/process-config.yaml")
    {
        // TODO: Phase 1 implementation
        var result = new HierarchyResult
        {
            WorkItemId = workItem,
            Title = "Not yet implemented",
            Type = "Unknown",
            Capabilities = [],
            State = "Unknown",
            Children = null
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HierarchyResult));
        return 0;
    }
}
