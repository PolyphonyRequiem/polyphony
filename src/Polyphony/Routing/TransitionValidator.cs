using Polyphony.Configuration;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services.Process;

namespace Polyphony.Routing;

/// <summary>
/// Validates whether a lifecycle event transition is legal for a work item
/// given its current state and the process config transition table.
/// </summary>
public sealed class TransitionValidator(ProcessConfig processConfig)
{
    /// <summary>
    /// Validates that the given <paramref name="eventName"/> is a legal transition
    /// for the specified <paramref name="item"/> and its <paramref name="children"/>.
    /// </summary>
    /// <param name="item">The work item to validate against.</param>
    /// <param name="eventName">The lifecycle event name (e.g., begin_planning, implementation_complete).</param>
    /// <param name="children">The immediate children of the work item.</param>
    /// <returns>A <see cref="ValidateResult"/> indicating validity, target state, and any messages.</returns>
    public ValidateResult Validate(WorkItem item, string eventName, IReadOnlyList<WorkItem> children)
    {
        var typeName = item.Type.Value;

        // Step 1: Look up transitions for this work item type
        if (!processConfig.Transitions.TryGetValue(typeName, out var typeTransitions))
        {
            return new ValidateResult
            {
                WorkItemId = item.Id,
                Event = eventName,
                IsValid = false,
                Message = $"No transitions defined for work item type '{typeName}'.",
            };
        }

        // Step 2: Look up the specific event
        if (!typeTransitions.TryGetValue(eventName, out var targetState))
        {
            return new ValidateResult
            {
                WorkItemId = item.Id,
                Event = eventName,
                IsValid = false,
                Message = $"Unknown event '{eventName}' for work item type '{typeName}'.",
            };
        }

        // Step 3: Check preconditions based on event name
        var preconditionMessage = CheckPrecondition(eventName, item, children);
        if (preconditionMessage is not null)
        {
            return new ValidateResult
            {
                WorkItemId = item.Id,
                Event = eventName,
                IsValid = false,
                TargetState = targetState,
                Message = preconditionMessage,
            };
        }

        // Step 4: Valid transition
        return new ValidateResult
        {
            WorkItemId = item.Id,
            Event = eventName,
            IsValid = true,
            TargetState = targetState,
            Message = $"Transition '{eventName}' is valid. Target state: '{targetState}'.",
        };
    }

    private static string? CheckPrecondition(string eventName, WorkItem item, IReadOnlyList<WorkItem> children)
    {
        var itemCategory = StateCategoryResolver.Resolve(item.State, entries: null);

        return eventName switch
        {
            "all_children_complete" => CheckAllChildrenComplete(children),
            "begin_planning" => CheckBeginPlanning(itemCategory),
            "begin_implementation" => CheckBeginImplementation(itemCategory),
            "implementation_complete" => CheckImplementationComplete(itemCategory),
            _ => null, // No precondition for unknown events
        };
    }

    private static string? CheckAllChildrenComplete(IReadOnlyList<WorkItem> children)
    {
        if (children.Count == 0)
            return "Precondition 'all_children_complete' failed: work item has no children.";

        for (var i = 0; i < children.Count; i++)
        {
            var childCategory = StateCategoryResolver.Resolve(children[i].State, entries: null);
            if (childCategory != StateCategory.Completed)
            {
                return $"Precondition 'all_children_complete' failed: child #{children[i].Id} " +
                       $"is in state '{children[i].State}' (category: {childCategory}), expected Completed.";
            }
        }

        return null;
    }

    private static string? CheckBeginPlanning(StateCategory itemCategory)
    {
        if (itemCategory != StateCategory.Proposed)
        {
            return $"Precondition 'begin_planning' failed: item must be in Proposed state category, " +
                   $"but is in {itemCategory}.";
        }

        return null;
    }

    private static string? CheckBeginImplementation(StateCategory itemCategory)
    {
        if (itemCategory != StateCategory.Proposed && itemCategory != StateCategory.InProgress)
        {
            return $"Precondition 'begin_implementation' failed: item must be in Proposed or InProgress " +
                   $"state category, but is in {itemCategory}.";
        }

        return null;
    }

    private static string? CheckImplementationComplete(StateCategory itemCategory)
    {
        if (itemCategory != StateCategory.InProgress)
        {
            return $"Precondition 'implementation_complete' failed: item must be in InProgress state category, " +
                   $"but is in {itemCategory}.";
        }

        return null;
    }
}
