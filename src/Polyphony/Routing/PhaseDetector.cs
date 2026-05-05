using Polyphony.Configuration;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services.Process;

namespace Polyphony.Routing;

/// <summary>
/// Core state machine that determines the SDLC phase and next action
/// for a work item based on its type, facets, state, and children.
/// </summary>
public sealed class PhaseDetector(ProcessConfig processConfig)
{
    /// <summary>
    /// ADO tag stamped on a work item by the seeder once planning has
    /// completed (atomic or decomposed). Read here so the detector can
    /// distinguish "never planned" from "planned but state never transitioned" —
    /// the latter would otherwise route back to NeedsPlanning forever.
    /// </summary>
    internal const string PlannedTag = "polyphony:planned";

    /// <summary>
    /// Determines the SDLC phase and recommended action for the given work item.
    /// </summary>
    /// <param name="item">The work item to evaluate.</param>
    /// <param name="children">The immediate children of the work item.</param>
    /// <returns>A <see cref="RoutingDecision"/> describing the detected phase and action.</returns>
    public RoutingDecision Detect(WorkItem item, IReadOnlyList<WorkItem> children)
    {
        var category = StateCategoryResolver.Resolve(item.State, entries: null);

        // Terminal states apply regardless of type or facets
        if (category == StateCategory.Completed)
            return new RoutingDone("Work item is complete.");

        if (category == StateCategory.Removed)
            return new RoutingRemoved("Work item has been removed.");

        var facets = LookupFacets(item.Type.Value);
        var isPlannable = Array.Exists(facets, c => string.Equals(c, "plannable", StringComparison.OrdinalIgnoreCase));
        var isImplementable = Array.Exists(facets, c => string.Equals(c, "implementable", StringComparison.OrdinalIgnoreCase));

        if (isPlannable)
            return DetectPlannablePhase(item, children, category, isImplementable);

        if (isImplementable)
            return DetectImplementablePhase(category);

        // Unknown facet set — fall through to unknown
        return new RoutingUnknown($"No recognized facets for type '{item.Type.Value}'.");
    }

    private static RoutingDecision DetectPlannablePhase(
        WorkItem item,
        IReadOnlyList<WorkItem> children,
        StateCategory category,
        bool isAlsoImplementable)
    {
        // If the planned tag is present, planning has already completed — even
        // if the work item state was never transitioned out of Proposed
        // (a common workflow gap). Treat as InProgress so the rest of the
        // logic classifies by children rather than re-routing to planning.
        var planned = IsPlanned(item);
        if (category == StateCategory.Proposed && planned)
        {
            category = StateCategory.InProgress;
        }

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
                    var atomicSuffix = planned ? " (planned, atomic)" : "";
                    return new ReadyForImplementation($"{item.Type.Value} '{item.Title}' is in progress with no children — ready for direct implementation{atomicSuffix}.");
                }

                // Plannable-only with no children → needs seeding (decomposition).
                // If the planned tag is set on a plannable-only type with no
                // children, the architect emitted an empty task list for a type
                // that requires decomposition — surface as NeedsSeeding so the
                // anomaly is visible rather than silently advancing.
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

    /// <summary>
    /// Checks whether the work item carries the <see cref="PlannedTag"/>.
    /// Tags are stored in <c>System.Tags</c> as a semicolon-separated string;
    /// match is case-insensitive and tolerant of surrounding whitespace.
    /// </summary>
    private static bool IsPlanned(WorkItem item)
    {
        if (!item.Fields.TryGetValue("System.Tags", out var tags) || string.IsNullOrWhiteSpace(tags))
            return false;

        var parts = tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], PlannedTag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private string[] LookupFacets(string typeName)
    {
        if (processConfig.Types.TryGetValue(typeName, out var typeConfig))
            return typeConfig.Facets;

        return [];
    }
}


