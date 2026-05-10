using Polyphony.Configuration;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;

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
    /// <returns>A <see cref="TransitionOutcome"/> indicating the validation result.</returns>
    public TransitionOutcome Validate(WorkItem item, string eventName, IReadOnlyList<WorkItem> children)
    {
        var typeName = item.Type.Value;

        // Step 1: Look up transitions for this work item type
        if (!processConfig.Transitions.TryGetValue(typeName, out var typeTransitions))
        {
            return new InvalidTransition(
                item.Id,
                eventName,
                null,
                $"No transitions defined for work item type '{typeName}'.");
        }

        // Step 2: Look up the specific event
        if (!typeTransitions.TryGetValue(eventName, out var targetState))
        {
            return new InvalidTransition(
                item.Id,
                eventName,
                null,
                $"Unknown event '{eventName}' for work item type '{typeName}'.");
        }

        // Step 3: Check preconditions based on event name
        var preconditionFailure = CheckPrecondition(item.Id, eventName, targetState, item, children);
        if (preconditionFailure is not null)
        {
            return (TransitionOutcome)preconditionFailure;
        }

        // Step 4: Valid transition
        return new ValidTransition(
            item.Id,
            eventName,
            targetState,
            $"Transition '{eventName}' is valid. Target state: '{targetState}'.");
    }

    private TransitionOutcome? CheckPrecondition(
        int workItemId, string eventName, string targetState, WorkItem item, IReadOnlyList<WorkItem> children)
    {
        var itemCategory = processConfig.GetCategory(item.Type.Value, item.State);

        return eventName switch
        {
            "all_children_complete" => CheckAllChildrenComplete(workItemId, eventName, targetState, children),
            "begin_planning" => CheckBeginPlanning(workItemId, eventName, targetState, itemCategory),
            "begin_implementation" => CheckBeginImplementation(workItemId, eventName, targetState, itemCategory),
            "implementation_complete" => CheckImplementationComplete(workItemId, eventName, targetState, itemCategory),
            "item_satisfied" => CheckItemSatisfied(workItemId, eventName, targetState, itemCategory),
            _ => null,
        };
    }

    private TransitionOutcome? CheckAllChildrenComplete(
        int workItemId, string eventName, string targetState, IReadOnlyList<WorkItem> children)
    {
        if (children.Count == 0)
            return new InvalidTransition(workItemId, eventName, targetState,
                "Precondition 'all_children_complete' failed: work item has no children.");

        for (var i = 0; i < children.Count; i++)
        {
            var childCategory = processConfig.GetCategory(children[i].Type.Value, children[i].State);
            if (childCategory != StateCategory.Completed)
            {
                return new InvalidTransition(workItemId, eventName, targetState,
                    $"Precondition 'all_children_complete' failed: child #{children[i].Id} " +
                    $"is in state '{children[i].State}' (category: {childCategory}), expected Completed.");
            }
        }

        return null;
    }

    private static TransitionOutcome? CheckBeginPlanning(
        int workItemId, string eventName, string targetState, StateCategory itemCategory)
    {
        if (itemCategory != StateCategory.Proposed)
        {
            return new InvalidTransition(workItemId, eventName, targetState,
                $"Precondition 'begin_planning' failed: item must be in Proposed state category, " +
                $"but is in {itemCategory}.");
        }

        return null;
    }

    private static TransitionOutcome? CheckBeginImplementation(
        int workItemId, string eventName, string targetState, StateCategory itemCategory)
    {
        if (itemCategory != StateCategory.Proposed && itemCategory != StateCategory.InProgress)
        {
            return new InvalidTransition(workItemId, eventName, targetState,
                $"Precondition 'begin_implementation' failed: item must be in Proposed or InProgress " +
                $"state category, but is in {itemCategory}.");
        }

        return null;
    }

    private static TransitionOutcome? CheckImplementationComplete(
        int workItemId, string eventName, string targetState, StateCategory itemCategory)
    {
        if (itemCategory != StateCategory.InProgress)
        {
            return new InvalidTransition(workItemId, eventName, targetState,
                $"Precondition 'implementation_complete' failed: item must be in InProgress state category, " +
                $"but is in {itemCategory}.");
        }

        return null;
    }

    private static TransitionOutcome? CheckItemSatisfied(
        int workItemId, string eventName, string targetState, StateCategory itemCategory)
    {
        if (itemCategory != StateCategory.InProgress)
        {
            return new InvalidTransition(workItemId, eventName, targetState,
                $"Precondition 'item_satisfied' failed: item must be in InProgress state category, " +
                $"but is in {itemCategory}.");
        }

        return null;
    }
}
