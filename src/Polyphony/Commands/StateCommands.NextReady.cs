using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Sdlc;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony state next-ready</c> — for a single work item, returns which
/// requirement(s) are currently dispatchable, plus the full reduced requirement
/// set for diagnostic context. The driver consumes this to choose what to
/// dispatch next (or whether to monitor or wait).
/// </summary>
public sealed partial class StateCommands
{
    /// <summary>
    /// Compute the next-ready requirements for a work item.
    /// </summary>
    /// <param name="workItem">ADO work item ID to inspect.</param>
    /// <param name="planRoot">Directory glob root for filesystem plan discovery (default <c>docs/projects</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("next-ready")]
    public async Task<int> NextReady(
        int workItem,
        string planRoot = "docs/projects",
        CancellationToken ct = default)
    {
        var item = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
        if (item is null)
        {
            // Match the JsonOutputContractTests.AllCommands_NotFound_ErrorJsonFormatConsistent contract.
            Console.WriteLine($$"""{"error":"Work item {{workItem}} not found","work_item_id":{{workItem}}}""");
            return ExitCodes.CacheError;
        }

        var workItemType = item.Type.Value ?? "";
        if (string.IsNullOrEmpty(workItemType) || !processConfig.Types.TryGetValue(workItemType, out var typeConfig))
        {
            EmitNextReadyError(workItem, workItemType, $"Type '{workItemType}' not found in process config.");
            return ExitCodes.ConfigError;
        }

        var children = await repository.GetChildrenAsync(workItem, ct).ConfigureAwait(false);
        var resolved = RequirementInputResolver.Resolve(typeConfig, children.Count);

        var derivation = RequirementSetDeriver.Derive(
            typeConfig.Facets,
            resolved.Decomposable,
            resolved.FacetOrder,
            resolved.ActionableExecutor);

        if (!derivation.IsValid)
        {
            EmitNextReadyError(workItem, workItemType,
                "Derivation failed: " + string.Join("; ", derivation.Errors),
                resolved);
            return ExitCodes.ConfigError;
        }

        var derived = derivation.Set!;

        // Pure container case: zero own-work requirements.
        if (derived.Items.Count == 0)
        {
            EmitNextReadyResult(workItem, workItemType, derived, resolved, status: "empty");
            return ExitCodes.Success;
        }

        // Compute observable state and reduce.
        var observed = await ComputeObservedAsync(workItem, item, children, planRoot, ct).ConfigureAwait(false);
        var reduced = RequirementSetReducer.Apply(derived, observed);

        var status = ClassifyStatus(reduced);
        EmitNextReadyResult(workItem, workItemType, reduced, resolved, status);
        return ExitCodes.Success;
    }

    private void EmitNextReadyResult(
        int workItem,
        string workItemType,
        RequirementSet set,
        ResolvedRequirementInputs resolved,
        string status)
    {
        var byDisp = set.Items
            .GroupBy(r => r.Disposition, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Kind).ToArray(), StringComparer.Ordinal);

        var result = new StateNextReadyResult
        {
            WorkItemId = workItem,
            WorkItemType = workItemType,
            Status = status,
            Requirements = set.Items,
            Next = byDisp.GetValueOrDefault(Disposition.Ready) ?? [],
            Fulfilling = byDisp.GetValueOrDefault(Disposition.Fulfilling) ?? [],
            Satisfied = byDisp.GetValueOrDefault(Disposition.Satisfied) ?? [],
            Needed = byDisp.GetValueOrDefault(Disposition.Needed) ?? [],
            ResolvedInputs = resolved,
            AnyInputInferred = resolved.AnyInferred,
        };

        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.StateNextReadyResult));
    }

    private void EmitNextReadyError(
        int workItem, string workItemType, string error,
        ResolvedRequirementInputs? resolved = null)
    {
        var safeResolved = resolved ?? new ResolvedRequirementInputs
        {
            Decomposable = false,
            DecomposableProvenance = ResolutionProvenance.NotApplicable,
            FacetOrder = null,
            FacetOrderProvenance = ResolutionProvenance.NotApplicable,
            ActionableExecutor = null,
            ActionableExecutorProvenance = ResolutionProvenance.NotApplicable,
        };
        var result = new StateNextReadyResult
        {
            WorkItemId = workItem,
            WorkItemType = workItemType,
            Status = "error",
            Requirements = [],
            Next = [],
            Fulfilling = [],
            Satisfied = [],
            Needed = [],
            ResolvedInputs = safeResolved,
            AnyInputInferred = safeResolved.AnyInferred,
            Error = error,
        };
        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.StateNextReadyResult));
    }

    private static string ClassifyStatus(RequirementSet set)
    {
        if (set.Items.Count == 0) return "empty";

        // Pure-container case: only the synthetic terminal exists, and it is
        // still Needed (cross-item rollup from children is not in scope yet).
        // Surface as "empty" — the item has no own-work to drive forward.
        if (set.Items.Count == 1
            && string.Equals(set.Items[0].Kind, RequirementKind.ItemSatisfied, StringComparison.Ordinal)
            && set.Items[0].Disposition == Disposition.Needed)
        {
            return "empty";
        }

        var hasReady = false;
        var hasFulfilling = false;
        var allSatisfied = true;
        foreach (var r in set.Items)
        {
            if (r.Disposition != Disposition.Satisfied) allSatisfied = false;
            if (r.Disposition == Disposition.Ready) hasReady = true;
            if (r.Disposition == Disposition.Fulfilling) hasFulfilling = true;
        }
        if (allSatisfied) return "satisfied";
        if (hasReady) return "dispatchable";
        if (hasFulfilling) return "monitoring";
        return "blocked";
    }

    private async Task<ObservedRequirementState> ComputeObservedAsync(
        int workItem,
        Twig.Domain.Aggregates.WorkItem item,
        IReadOnlyList<Twig.Domain.Aggregates.WorkItem> children,
        string planRoot,
        CancellationToken ct)
    {
        var (planStatus, _, _) = DiscoverPlan(workItem, "", planRoot);
        var planAuthoredDisposition = planStatus == "complete"
            ? Disposition.Satisfied
            : Disposition.Needed;

        // children_seeded: every non-Done child has tasks of its own.
        var childCount = children.Count;
        var anyChildMissingTasks = childCount > 0 && children.Any(c => c.State != "Done");
        var allChildrenDone = childCount > 0 && children.All(c => c.State == "Done");
        var seedDisposition = (childCount, anyChildMissingTasks, allChildrenDone) switch
        {
            (0, _, _) => Disposition.Needed,
            (_, _, true) => Disposition.Satisfied,
            _ => Disposition.Fulfilling,
        };

        // implementation_merged: best-effort from item state + child state.
        // Authoritative PR/branch inspection lives in state detect; here we
        // approximate from observable work-item state. State detect will
        // overlay its richer signal via BuildObservedFromDetectSignals.
        var implDisposition = (item.State, childCount, allChildrenDone) switch
        {
            ("Done", _, _) => Disposition.Satisfied,
            (_, > 0, true) => Disposition.Satisfied,
            ("Doing", _, _) => Disposition.Fulfilling,
            _ => Disposition.Needed,
        };

        await Task.CompletedTask.ConfigureAwait(false);  // async-shape for future I/O
        return BuildObservedFromSignals(planAuthoredDisposition, seedDisposition, implDisposition);
    }

    /// <summary>Build an <see cref="ObservedRequirementState"/> from the three
    /// signal dispositions we currently track. Plan_reviewed, plan_promoted,
    /// action_satisfied, and evidence_accepted have no observable signals yet
    /// (Phase 3 / Phase 6 work) and default to <see cref="Disposition.Needed"/>.</summary>
    private static ObservedRequirementState BuildObservedFromSignals(
        string planAuthored,
        string childrenSeeded,
        string implementationMerged)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (planAuthored != Disposition.Needed) dict[RequirementKind.PlanAuthored] = planAuthored;
        if (childrenSeeded != Disposition.Needed) dict[RequirementKind.ChildrenSeeded] = childrenSeeded;
        if (implementationMerged != Disposition.Needed) dict[RequirementKind.ImplementationMerged] = implementationMerged;
        return new ObservedRequirementState { Observed = dict };
    }
}
