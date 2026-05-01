using Polyphony.Configuration;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services.Process;

namespace Polyphony.Routing;

/// <summary>
/// Core state machine that determines the SDLC phase and next action
/// for a work item based on its type, capabilities, state, and children.
/// </summary>
public sealed class PhaseDetector(ProcessConfig processConfig)
{
    /// <summary>
    /// Determines the SDLC phase and recommended action for the given work item.
    /// </summary>
    /// <param name="item">The work item to evaluate.</param>
    /// <param name="children">The immediate children of the work item.</param>
    /// <returns>A <see cref="RoutingDecision"/> describing the detected phase and action.</returns>
    public RoutingDecision Detect(WorkItem item, IReadOnlyList<WorkItem> children)
    {
        var category = StateCategoryResolver.Resolve(item.State, entries: null);

        // Terminal states apply regardless of type or capabilities
        if (category == StateCategory.Completed)
            return new RoutingDone("Work item is complete.");

        if (category == StateCategory.Removed)
            return new RoutingRemoved("Work item has been removed.");

        var capabilities = LookupCapabilities(item.Type.Value);
        var isPlannable = Array.Exists(capabilities, c => string.Equals(c, "plannable", StringComparison.OrdinalIgnoreCase));
        var isImplementable = Array.Exists(capabilities, c => string.Equals(c, "implementable", StringComparison.OrdinalIgnoreCase));

        if (isPlannable)
            return DetectPlannablePhase(item, children, category, isImplementable);

        if (isImplementable)
            return DetectImplementablePhase(category);

        // Unknown capability set — fall through to unknown
        return new RoutingUnknown($"No recognized capabilities for type '{item.Type.Value}'.");
    }

    private static RoutingDecision DetectPlannablePhase(
        WorkItem item,
        IReadOnlyList<WorkItem> children,
        StateCategory category,
        bool isAlsoImplementable)
    {
        if (category == StateCategory.Proposed)
        {
            return new NeedsPlanning($"{item.Type.Value} '{item.Title}' is in Proposed state and needs planning.");
        }

        if (category == StateCategory.InProgress || category == StateCategory.Resolved)
        {
            if (children.Count == 0)
            {
                // Plannable + implementable with no children in InProgress → ready for direct implementation
                if (isAlsoImplementable)
                {
                    return new ReadyForImplementation($"{item.Type.Value} '{item.Title}' is in progress with no children — ready for direct implementation.");
                }

                // Plannable-only with no children → needs seeding (decomposition)
                return new NeedsSeeding($"{item.Type.Value} '{item.Title}' is in progress but has no children — needs seeding.");
            }

            return ClassifyByChildren(item, children);
        }

        return new RoutingUnknown($"Unrecognized state category '{category}' for plannable type '{item.Type.Value}'.");
    }

    private static RoutingDecision DetectImplementablePhase(StateCategory category)
    {
        return category switch
        {
            StateCategory.Proposed => new ReadyForImplementation("Implementable item is in Proposed state — ready for implementation."),
            StateCategory.InProgress => new ImplementationInProgress("Implementable item is in progress."),
            StateCategory.Resolved => new ImplementationInProgress("Implementable item is resolved, awaiting completion."),
            _ => new RoutingUnknown($"Unrecognized state category '{category}' for implementable type."),
        };
    }

    private static RoutingDecision ClassifyByChildren(WorkItem item, IReadOnlyList<WorkItem> children)
    {
        var allCompleted = true;
        var allProposed = true;

        for (var i = 0; i < children.Count; i++)
        {
            var childCategory = StateCategoryResolver.Resolve(children[i].State, entries: null);

            if (childCategory != StateCategory.Completed && childCategory != StateCategory.Removed)
                allCompleted = false;

            if (childCategory != StateCategory.Proposed)
                allProposed = false;
        }

        if (allCompleted)
        {
            return new ReadyForCompletion($"All children of {item.Type.Value} '{item.Title}' are complete — ready for close-out.");
        }

        if (allProposed)
        {
            return new ReadyForImplementation($"All children of {item.Type.Value} '{item.Title}' are in Proposed state — ready for implementation.");
        }

        // Mixed states — work is in progress
        return new ImplementationInProgress($"{item.Type.Value} '{item.Title}' has children in mixed states — monitoring progress.");
    }

    private string[] LookupCapabilities(string typeName)
    {
        if (processConfig.Types.TryGetValue(typeName, out var typeConfig))
            return typeConfig.Capabilities;

        return [];
    }
}
