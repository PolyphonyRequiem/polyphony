using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Routing;

namespace Polyphony.Commands;

/// <summary>
/// Planning-stage verbs (<c>polyphony plan ...</c>). Replaces the deterministic
/// PowerShell scripts invoked from <c>plan-level.yaml</c>:
/// <list type="bullet">
///   <item><c>scripts/depth-guard.ps1</c> → <see cref="DepthGuard"/></item>
///   <item><c>scripts/child-router.ps1</c> → <see cref="NextChild"/></item>
/// </list>
/// </summary>
/// <remarks>
/// Routing-script convention: both verbs always return <see cref="ExitCodes.Success"/>.
/// The workflow YAML routes on the JSON payload (e.g. <c>allowed</c>,
/// <c>has_plannable_children</c>), not on the exit code.
/// </remarks>
public sealed class PlanCommands(HierarchyWalker walker)
{
    /// <summary>
    /// Validates current recursion depth against a configured maximum. Always exits 0.
    /// </summary>
    /// <param name="depth">Current recursion depth (0 = root level).</param>
    /// <param name="maxDepth">Maximum allowed recursion depth.</param>
    [Command("depth-guard")]
    public int DepthGuard(int depth, int maxDepth = 6)
    {
        var allowed = depth < maxDepth;
        var remaining = allowed ? maxDepth - depth : 0;
        var message = allowed
            ? $"Depth {depth} is within limit (max {maxDepth}). {remaining} level(s) remaining."
            : $"Recursion depth {depth} reached maximum {maxDepth}";

        var result = new PlanDepthGuardResult
        {
            Allowed = allowed,
            Depth = depth,
            MaxDepth = maxDepth,
            Remaining = remaining,
            Message = message,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanDepthGuardResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Lists immediate children of a work item that have the <c>plannable</c>
    /// capability in the process config. Drives the <c>for_each</c> recursion in
    /// <c>plan-level.yaml</c>. Always exits 0; surfaces any error inline.
    /// </summary>
    /// <param name="workItem">ADO work item ID whose children to discover.</param>
    [Command("next-child")]
    public async Task<int> NextChild(int workItem, CancellationToken ct = default)
    {
        var hierarchy = await walker.WalkAsync(workItem, maxDepth: 1, ct);
        if (hierarchy is null)
        {
            // Routing-script contract: always exit 0; surface the error inline,
            // route the workflow to the "no plannable children" branch.
            var notFound = new PlanNextChildResult
            {
                HasPlannableChildren = false,
                PlannableChildren = [],
                ParentId = workItem,
                Count = 0,
                Error = $"Work item {workItem} not found",
            };
            Console.WriteLine(JsonSerializer.Serialize(notFound, PolyphonyJsonContext.Default.PlanNextChildResult));
            return ExitCodes.Success;
        }

        var children = (hierarchy.Children ?? [])
            .Where(c => c.Capabilities.Contains("plannable"))
            .Select(c => new PlannableChild { Id = c.WorkItemId, Type = c.Type, Title = c.Title })
            .ToArray();

        var result = new PlanNextChildResult
        {
            HasPlannableChildren = children.Length > 0,
            PlannableChildren = children,
            ParentId = workItem,
            Count = children.Length,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanNextChildResult));
        return ExitCodes.Success;
    }
}
