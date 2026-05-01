using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Configuration;
using Polyphony.Routing;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Determines the current SDLC phase and next action for a work item.
/// </summary>
public sealed class RouteCommand(
    PhaseDetector phaseDetector,
    IWorkItemRepository repository,
    ProcessConfig processConfig)
{
    /// <summary>
    /// Route a work item through the SDLC state machine.
    /// </summary>
    /// <param name="workItem">ADO work item ID</param>
    /// <param name="config">Path to .conductor/process-config.yaml</param>
    [Command("route")]
    public async Task<int> Route(int workItem, string config = ".conductor/process-config.yaml", CancellationToken ct = default)
    {
        var item = await repository.GetByIdAsync(workItem, ct);
        if (item is null)
        {
            Console.WriteLine($$"""{"error":"Work item {{workItem}} not found","work_item_id":{{workItem}}}""");
            return ExitCodes.CacheError;
        }

        var children = await repository.GetChildrenAsync(workItem, ct);
        var decision = phaseDetector.Detect(item, children);
        var workspaceHint = BranchNameResolver.Resolve(processConfig, item);
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        var result = new RouteResult
        {
            WorkItemId = workItem,
            Phase = phase,
            Action = action,
            Message = message,
            WorkspaceHint = workspaceHint,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.RouteResult));
        return ExitCodes.Success;
    }
}
