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
        var outcome = validator.Validate(item, @event, children);

        var result = outcome switch
        {
            ValidTransition v => new ValidateResult
            {
                WorkItemId = v.WorkItemId,
                Event = v.Event,
                IsValid = true,
                TargetState = v.TargetState,
                Message = v.Message,
            },
            InvalidTransition iv => new ValidateResult
            {
                WorkItemId = iv.WorkItemId,
                Event = iv.Event,
                IsValid = false,
                TargetState = iv.TargetState,
                Message = iv.Message,
            },
            null => throw new InvalidOperationException("TransitionValidator returned null"),
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ValidateResult));
        return result.IsValid ? ExitCodes.Success : ExitCodes.RoutingFailure;
    }
}
