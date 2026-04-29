using System.Text.Json;
using ConsoleAppFramework;

namespace Polyphony.Commands;

/// <summary>
/// Validates that a lifecycle event transition is legal for a work item.
/// </summary>
public sealed class ValidateCommand
{
    /// <summary>
    /// Validate that a lifecycle event transition is legal for a work item.
    /// </summary>
    /// <param name="workItem">ADO work item ID</param>
    /// <param name="event">Lifecycle event name (e.g., begin_planning, implementation_complete)</param>
    /// <param name="config">Path to .conductor/process-config.yaml</param>
    [Command("validate")]
    public int Validate(int workItem, string @event, string config = ".conductor/process-config.yaml")
    {
        // TODO: Phase 1 implementation
        var result = new ValidateResult
        {
            WorkItemId = workItem,
            Event = @event,
            IsValid = false,
            Message = "Validation not yet implemented. This is a Phase 1 deliverable."
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ValidateResult));
        return 0;
    }
}
