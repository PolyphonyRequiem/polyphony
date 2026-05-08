using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Sdlc.Observers;

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
    /// <param name="planRoot">Reserved — formerly drove filesystem plan
    /// discovery before <see cref="PlanObserver"/> took over the
    /// plan_authored signal. Accepted for backward-compatibility with
    /// existing workflow callers; ignored by the verb.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("next-ready")]
    [VerbResult(typeof(StateNextReadyResult))]
    public async Task<int> NextReady(
        int workItem = RequiredInput.MissingInt,
        string planRoot = "docs/projects",
        CancellationToken ct = default)
    {
        _ = planRoot;
        if (RequiredInput.HaltIfMissing("state next-ready",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

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
        var (observed, reasons) = await ComputeObservedAsync(workItem, item, children, ct).ConfigureAwait(false);
        var reduced = RequirementSetReducer.Apply(derived, observed);

        var status = ClassifyStatus(reduced);
        EmitNextReadyResult(workItem, workItemType, reduced, resolved, status, reasons);
        return ExitCodes.Success;
    }

    private void EmitNextReadyResult(
        int workItem,
        string workItemType,
        RequirementSet set,
        ResolvedRequirementInputs resolved,
        string status,
        IReadOnlyDictionary<string, string>? observationReasons = null)
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
            ObservationReasons = observationReasons is { Count: > 0 } ? observationReasons : null,
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
            ExecutionMode = ExecutionMode.Parallel,
            ExecutionModeProvenance = ResolutionProvenance.Default,
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

    private async Task<(ObservedRequirementState Observed, IReadOnlyDictionary<string, string> Reasons)> ComputeObservedAsync(
        int workItem,
        Twig.Domain.Aggregates.WorkItem item,
        IReadOnlyList<Twig.Domain.Aggregates.WorkItem> children,
        CancellationToken ct)
    {
        _ = children;  // PR #4: legacy children-state heuristic removed; cross-item rollup is PR #5's job.
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        // ── Plan-kind observers (PR #2 — wired here). Build the per-item
        // observation scope ONCE so plan_authored / plan_reviewed /
        // plan_promoted share the underlying gh/git I/O. Sibling observers
        // (PR #3 ChildrenSeeded, PR #4 Implementation) plug in by extending
        // NextReadyObservationScope and adding their own composer methods —
        // see the XML doc on NextReadyObservationScope for the contract.
        var scope = await BuildObservationScopeAsync(workItem, item, ct).ConfigureAwait(false);

        var (planAuthoredDisp, planAuthoredReason) = ComposePlanAuthored(scope);
        reasons[RequirementKind.PlanAuthored] = planAuthoredReason;

        var (planReviewedDisp, planReviewedReason) = ComposePlanReviewed(scope);
        reasons[RequirementKind.PlanReviewed] = planReviewedReason;

        var (planPromotedDisp, planPromotedReason) = ComposePlanPromoted(scope);
        reasons[RequirementKind.PlanPromoted] = planPromotedReason;

        // ── PR #3 children_seeded observer: the polyphony:planned tag is
        // the canonical write-once signal that the seeder ran, regardless
        // of whether it produced children (the indivisible case from
        // closed-loop §3.4). Replaces the pre-PR-#3 "any non-Done child"
        // heuristic which mis-labeled apex items with no children seeded
        // yet (e.g. #3043) as Needed despite the seeder also not having
        // run — silently equating "never planned" with "ready to plan".
        var (childrenSeededDisp, childrenSeededReason) = ComposeChildrenSeeded(scope);
        reasons[RequirementKind.ChildrenSeeded] = childrenSeededReason;

        // ── PR #4 implementation_merged observer: read the impl PR for
        // the canonical impl/{root}-{item} branch (gh pr list + gh pr
        // view). Replaces the pre-PR-#4 "item.State + child.State"
        // heuristic which had no closed-loop after a merge and no PR
        // introspection at all. Per-item only in PR #4 — cross-item
        // rollup (the MG-PR roll-up case from closed-loop §3.1 row 5) is
        // deferred to PR #5's reducer.
        var (implMergedDisp, implMergedReason) = ComposeImplementationMerged(scope);
        reasons[RequirementKind.ImplementationMerged] = implMergedReason;

        var observed = BuildObservedFromSignals(
            planAuthoredDisp, planReviewedDisp, planPromotedDisp,
            childrenSeededDisp, implMergedDisp);
        return (observed, reasons);
    }

    /// <summary>
    /// Build the per-item <see cref="NextReadyObservationScope"/> for the
    /// PR #2 plan-kind observers. Fetches each shared signal exactly once
    /// and degrades to "no signal" (with the error captured on the scope)
    /// on any failure — never throws past the scope. <c>state next-ready</c>
    /// is a routing-style verb and must always exit 0 with a structured
    /// envelope.
    /// </summary>
    private async Task<NextReadyObservationScope> BuildObservationScopeAsync(
        int workItem,
        Twig.Domain.Aggregates.WorkItem item,
        CancellationToken ct)
    {
        var rootId = await ResolveRootIdAsync(item, ct).ConfigureAwait(false);
        var planBranch = PlanObserver.ResolvePlanBranch(rootId, workItem);
        var implBranch = PlanObserver.ResolveImplBranch(rootId, workItem);

        var scope = new NextReadyObservationScope
        {
            ItemId = workItem,
            RootId = rootId,
            PlanBranch = planBranch,
            ImplBranch = implBranch,
        };

        // PR #3: children_seeded depends only on the polyphony:planned tag
        // on the item itself — independent of plan branch / slug / PR
        // state. Fetch up-front so every early-return path below still
        // surfaces an observed children_seeded signal.
        await FetchPlannedTagAsync(scope, ct).ConfigureAwait(false);

        // PlanObserver.TryResolveSlugAsync swallows internally, but defend
        // again here so a future tightening of the observer does not break
        // the verb's "always exit 0" contract.
        try
        {
            scope.Slug = await planObserver.TryResolveSlugAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            scope.Slug = string.Empty;
        }

        // PR #4: implementation_merged is independent of plan branch /
        // plan PR existence — fetch the impl signal up-front, before the
        // plan-branch / slug early returns below. The impl PR query
        // itself still requires the slug, so it sits AFTER slug
        // resolution but BEFORE any plan-related early returns. Empty
        // implBranch (non-positive ids) and empty slug both degrade to
        // structured "could not observe" reasons inside FetchImplPrAsync.
        await FetchImplPrAsync(scope, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(planBranch))
        {
            // Non-positive root or item id — nothing more to observe. Plan
            // composers will degrade with the empty plan branch.
            return scope;
        }

        try
        {
            scope.BranchExistsOnOrigin = await planObserver.CheckPlanBranchExistsAsync(planBranch, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            scope.BranchExistsOnOrigin = false;
        }

        if (string.IsNullOrEmpty(scope.Slug))
        {
            // Without a slug we cannot list PRs; record the gap as a
            // structured fetch error so plan composers say "could not
            // resolve repo slug" rather than "no PR opened".
            scope.PlanPrFetchError = "could not resolve repo slug from origin remote";
            return scope;
        }

        try
        {
            scope.LatestPlanPr = await planObserver.GetLatestPlanPrAsync(scope.Slug, planBranch, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            scope.PlanPrFetchError = ex.FormatErrorMessage("gh pr list");
        }
        catch (ExternalToolException ex)
        {
            scope.PlanPrFetchError = $"gh pr list failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            scope.PlanPrFetchError = $"gh pr list failed: {ex.Message}";
        }

        if (scope.LatestPlanPr is null) return scope;

        try
        {
            scope.PlanPrPoll = await planObserver.GetPlanPrPollAsync(scope.Slug, scope.LatestPlanPr.Number, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            scope.PlanPrPollError = ex.FormatErrorMessage("gh pr view");
        }
        catch (ExternalToolException ex)
        {
            scope.PlanPrPollError = $"gh pr view failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            scope.PlanPrPollError = $"gh pr view failed: {ex.Message}";
        }

        return scope;
    }

    /// <summary>
    /// Fetch the children-seeded signal: presence of the
    /// <c>polyphony:planned</c> tag on the work item itself. Delegates to
    /// the PR #1 <see cref="PlanObserver.IsParentSeededAsync"/> primitive
    /// (the same tag-read that <c>plan detect-state</c> already uses) so
    /// the verb and the legacy state-machine stay on a single source of
    /// truth.
    /// </summary>
    /// <remarks>
    /// <see cref="PlanObserver.IsParentSeededAsync"/> swallows transient
    /// twig failures internally and degrades to "not seeded" — the
    /// outer try/catch here is defense-in-depth so any future tightening
    /// of the observer (or a non-swallowed path such as cancellation)
    /// surfaces as a structured <see cref="NextReadyObservationScope.PlannedTagFetchError"/>
    /// rather than escaping the verb.
    /// </remarks>
    private async Task FetchPlannedTagAsync(
        NextReadyObservationScope scope,
        CancellationToken ct)
    {
        try
        {
            scope.PlannedTagPresent = await planObserver
                .IsParentSeededAsync(scope.ItemId, "polyphony:planned", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scope.PlannedTagPresent = false;
            scope.PlannedTagFetchError = $"twig show failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Fetch the implementation_merged signal: the latest PR for the
    /// canonical <c>impl/{root}-{item}</c> branch and (when one exists)
    /// its rich poll snapshot. Empty <see cref="NextReadyObservationScope.ImplBranch"/>
    /// (non-positive ids) and empty <see cref="NextReadyObservationScope.Slug"/>
    /// both degrade to a structured "could not observe" reason on
    /// <see cref="NextReadyObservationScope.ImplPrFetchError"/> rather than
    /// throwing — same posture as the plan-PR fetch. Failures from gh
    /// pr list / gh pr view are captured into
    /// <see cref="NextReadyObservationScope.ImplPrFetchError"/> /
    /// <see cref="NextReadyObservationScope.ImplPrPollError"/> so the
    /// composer can distinguish "couldn't observe" from "observed: no
    /// PR" — the closed-loop spec requires that distinction.
    /// </summary>
    private async Task FetchImplPrAsync(
        NextReadyObservationScope scope,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scope.ImplBranch))
        {
            scope.ImplPrFetchError = "could not resolve impl branch (non-positive root or item id)";
            return;
        }

        if (string.IsNullOrEmpty(scope.Slug))
        {
            // Without a slug we cannot list PRs — record the gap as a
            // structured fetch error so the composer says "could not
            // resolve repo slug" rather than "no impl PR opened".
            scope.ImplPrFetchError = "could not resolve repo slug from origin remote";
            return;
        }

        try
        {
            scope.LatestImplPr = await planObserver
                .GetLatestImplPrAsync(scope.Slug, scope.ImplBranch, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            scope.ImplPrFetchError = ex.FormatErrorMessage("gh pr list");
            return;
        }
        catch (ExternalToolException ex)
        {
            scope.ImplPrFetchError = $"gh pr list failed: {ex.Message}";
            return;
        }
        catch (Exception ex)
        {
            scope.ImplPrFetchError = $"gh pr list failed: {ex.Message}";
            return;
        }

        if (scope.LatestImplPr is null) return;

        try
        {
            scope.ImplPrPoll = await planObserver
                .GetPlanPrPollAsync(scope.Slug, scope.LatestImplPr.Number, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            scope.ImplPrPollError = ex.FormatErrorMessage("gh pr view");
        }
        catch (ExternalToolException ex)
        {
            scope.ImplPrPollError = $"gh pr view failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            scope.ImplPrPollError = $"gh pr view failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Walk <see cref="Twig.Domain.Aggregates.WorkItem.ParentId"/> from
    /// <paramref name="item"/> until we hit a node with no parent — that
    /// is the root for branch-naming purposes. Returns <paramref name="item"/>'s
    /// own id when it is already the root, when the chain breaks, or when
    /// we exceed an internal cycle cap (50 ancestors). The cap matches
    /// <c>plan derive-ancestor-chain</c>'s posture.
    /// </summary>
    private async Task<int> ResolveRootIdAsync(Twig.Domain.Aggregates.WorkItem item, CancellationToken ct)
    {
        const int AncestorWalkLimit = 50;

        var cursor = item;
        var visited = new HashSet<int> { item.Id };
        for (var step = 0; step < AncestorWalkLimit; step++)
        {
            var parentId = cursor.ParentId;
            if (parentId is null || parentId == 0) return cursor.Id;
            if (!visited.Add(parentId.Value))
            {
                // Cycle — fall back to the highest id we have walked. The
                // safe choice for branch naming is the current cursor (the
                // last well-formed link before the cycle closes).
                return cursor.Id;
            }
            var parent = await repository.GetByIdAsync(parentId.Value, ct).ConfigureAwait(false);
            if (parent is null) return cursor.Id;
            cursor = parent;
        }
        // Reached the cap without finding a top — degrade to the deepest
        // ancestor we did reach. Branch naming will still produce a valid
        // plan/{root}-{item} string.
        return cursor.Id;
    }

    /// <summary>
    /// Map the plan-kind shared signals on <paramref name="scope"/> to a
    /// (Disposition, Reason) tuple for the <c>plan_authored</c>
    /// requirement. Failed I/O (recorded on
    /// <see cref="NextReadyObservationScope.PlanPrFetchError"/> /
    /// <see cref="NextReadyObservationScope.PlanPrPollError"/>) forces
    /// Needed with the captured error in the reason — distinguishing
    /// "couldn't observe" from "observed: no PR" per the closed-loop spec.
    /// </summary>
    private static (string Disposition, string Reason) ComposePlanAuthored(NextReadyObservationScope scope)
    {
        if (scope.PlanPrFetchError is not null)
        {
            return (Disposition.Needed, scope.PlanPrFetchError);
        }
        if (scope.LatestPlanPr is not null && scope.PlanPrPollError is not null)
        {
            return (Disposition.Needed, scope.PlanPrPollError);
        }

        var prState = scope.PlanPrPoll?.State?.ToUpperInvariant();
        var observation = PlanObserver.MapPlanAuthored(
            scope.PlanBranch, scope.BranchExistsOnOrigin, scope.LatestPlanPr, prState);
        return ValidateDisposition(observation.Disposition, observation.Reason);
    }

    private static (string Disposition, string Reason) ComposePlanReviewed(NextReadyObservationScope scope)
    {
        if (scope.PlanPrFetchError is not null)
        {
            return (Disposition.Needed, scope.PlanPrFetchError);
        }
        if (scope.LatestPlanPr is not null && scope.PlanPrPollError is not null)
        {
            return (Disposition.Needed, scope.PlanPrPollError);
        }

        var observation = PlanObserver.MapPlanReviewed(scope.LatestPlanPr, scope.PlanPrPoll);
        return ValidateDisposition(observation.Disposition, observation.Reason);
    }

    private static (string Disposition, string Reason) ComposePlanPromoted(NextReadyObservationScope scope)
    {
        if (scope.PlanPrFetchError is not null)
        {
            return (Disposition.Needed, scope.PlanPrFetchError);
        }
        if (scope.LatestPlanPr is not null && scope.PlanPrPollError is not null)
        {
            return (Disposition.Needed, scope.PlanPrPollError);
        }

        var prState = scope.PlanPrPoll?.State?.ToUpperInvariant();
        var observation = PlanObserver.MapPlanPromoted(scope.LatestPlanPr, prState);
        return ValidateDisposition(observation.Disposition, observation.Reason);
    }

    /// <summary>
    /// Map the children-seeded shared signals on <paramref name="scope"/>
    /// to a (Disposition, Reason) tuple for the <c>children_seeded</c>
    /// requirement. The tag-presence semantics (NOT child counts)
    /// correctly report <see cref="Disposition.Satisfied"/> for plans
    /// that legitimately seeded zero children — see
    /// <c>files/closed-loop-state-plan.md §3.4</c> and the rationale on
    /// <see cref="Sdlc.Observers.ChildrenSeededObservation"/>.
    /// </summary>
    private static (string Disposition, string Reason) ComposeChildrenSeeded(NextReadyObservationScope scope)
    {
        if (scope.PlannedTagFetchError is not null)
        {
            return (Disposition.Needed, scope.PlannedTagFetchError);
        }

        var observation = PlanObserver.MapChildrenSeeded(scope.PlannedTagPresent, "polyphony:planned");
        return ValidateDisposition(observation.Disposition, observation.Reason);
    }

    /// <summary>
    /// Map the implementation-merged shared signals on
    /// <paramref name="scope"/> to a (Disposition, Reason) tuple for the
    /// <c>implementation_merged</c> requirement. Failed I/O (recorded on
    /// <see cref="NextReadyObservationScope.ImplPrFetchError"/> /
    /// <see cref="NextReadyObservationScope.ImplPrPollError"/>) forces
    /// Needed with the captured error in the reason — distinguishing
    /// "couldn't observe" from "observed: no PR" per the closed-loop
    /// spec.
    /// </summary>
    /// <remarks>
    /// PR #4 scope: per-item impl PR only. For an MG item that has its
    /// own impl PR merged but unmerged impl-children, this composer will
    /// still report <see cref="Disposition.Satisfied"/> — the cross-item
    /// rollup that should tighten that to the worst child disposition is
    /// PR #5's job (see closed-loop §3.1 row 5). The reason string does
    /// not currently distinguish "self-PR merged" from "self + all
    /// children merged" — PR #5 will introduce the rollup signal and can
    /// extend this composer to surface the joint disposition.
    /// </remarks>
    private static (string Disposition, string Reason) ComposeImplementationMerged(NextReadyObservationScope scope)
    {
        if (scope.ImplPrFetchError is not null)
        {
            return (Disposition.Needed, scope.ImplPrFetchError);
        }
        if (scope.LatestImplPr is not null && scope.ImplPrPollError is not null)
        {
            return (Disposition.Needed, scope.ImplPrPollError);
        }

        var prState = scope.ImplPrPoll?.State?.ToUpperInvariant();
        var observation = PlanObserver.MapImplementationMerged(
            scope.ImplBranch, scope.LatestImplPr, prState);
        return ValidateDisposition(observation.Disposition, observation.Reason);
    }

    /// <summary>
    /// Guard against an observer ever returning a string that is not one of
    /// the four canonical <see cref="Disposition"/> values. Per the
    /// closed-loop spec we throw rather than silently swallow — silent
    /// fallback would re-introduce the exact "everything Needed" lie the
    /// PR set is fixing.
    /// </summary>
    private static (string Disposition, string Reason) ValidateDisposition(string disposition, string reason)
    {
        if (!Disposition.IsValid(disposition))
        {
            throw new InvalidOperationException(
                $"Observer returned unknown disposition '{disposition}'. " +
                "Valid values: needed, ready, fulfilling, satisfied.");
        }
        if (disposition == Disposition.Ready)
        {
            throw new InvalidOperationException(
                "Observer returned disposition 'ready'; readiness is reducer-derived, " +
                "observers must emit only needed/fulfilling/satisfied.");
        }
        return (disposition, reason);
    }

    /// <summary>Build an <see cref="ObservedRequirementState"/> from the
    /// composed signal dispositions. Kinds at <see cref="Disposition.Needed"/>
    /// are omitted because the reducer treats absence as Needed by default;
    /// stronger dispositions are surfaced explicitly so the reducer can
    /// promote downstream gates. As of PR #4 this is the sole composer of
    /// observed plan/seed/impl signals — the per-kind reasons dictionary
    /// travels alongside via <c>ComputeObservedAsync</c>.</summary>
    private static ObservedRequirementState BuildObservedFromSignals(
        string planAuthored,
        string planReviewed,
        string planPromoted,
        string childrenSeeded,
        string implementationMerged)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (planAuthored != Disposition.Needed) dict[RequirementKind.PlanAuthored] = planAuthored;
        if (planReviewed != Disposition.Needed) dict[RequirementKind.PlanReviewed] = planReviewed;
        if (planPromoted != Disposition.Needed) dict[RequirementKind.PlanPromoted] = planPromoted;
        if (childrenSeeded != Disposition.Needed) dict[RequirementKind.ChildrenSeeded] = childrenSeeded;
        if (implementationMerged != Disposition.Needed) dict[RequirementKind.ImplementationMerged] = implementationMerged;
        return new ObservedRequirementState { Observed = dict };
    }
}
