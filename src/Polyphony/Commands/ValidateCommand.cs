using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Routing;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Validates that a lifecycle event transition is legal for a work item.
/// </summary>
public sealed class ValidateCommand(
    TransitionValidator validator,
    IWorkItemRepository repository)
{
    /// <summary>
    /// Validate that a lifecycle event transition is legal for a work item.
    /// </summary>
    /// <param name="workItem">ADO work item ID</param>
    /// <param name="event">Lifecycle event name (e.g., begin_planning, implementation_complete)</param>
    /// <param name="config">Path to .conductor/process-config.yaml</param>
    [Command("validate")]
    public async Task<int> Validate(int workItem, string @event, string config = ".conductor/process-config.yaml", CancellationToken ct = default)
    {
        var item = await repository.GetByIdAsync(workItem, ct);
        if (item is null)
        {
            Console.WriteLine($$"""{"error":"Work item {{workItem}} not found","work_item_id":{{workItem}}}""");
            return ExitCodes.CacheError;
        }

        var children = await repository.GetChildrenAsync(workItem, ct);
        var result = validator.Validate(item, @event, children);

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ValidateResult));
        return result.IsValid ? ExitCodes.Success : ExitCodes.RoutingFailure;
    }
}
