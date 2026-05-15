using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Sdlc.Observers;
using Polyphony.Tagging;
using Twig.Domain.Aggregates;

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
    /// <param name="platform">Optional override that pins the host platform
    /// (<c>github</c> | <c>ado</c>). When supplied, the corresponding
    /// <c>--organization/--project/--repository</c> fields must be
    /// non-empty (per <see cref="RepoIdentityResolver"/>'s contract). When
    /// empty, the active <see cref="RepoIdentity"/> is parsed from
    /// <c>git remote get-url origin</c>.</param>
    /// <param name="organization">ADO organization (override path).
    /// Ignored on <c>platform=github</c>.</param>
    /// <param name="project">ADO project (override path). Ignored on
    /// <c>platform=github</c>.</param>
    /// <param name="repository">Repository name. For <c>platform=github</c>
    /// this is the gh-CLI <c>owner/repo</c> slug; for <c>platform=ado</c>
    /// it is the bare repository name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("next-ready")]
    [VerbResult(typeof(StateNextReadyResult))]
    public async Task<int> NextReady(
        int workItem = RequiredInput.MissingInt,
        string planRoot = "docs/projects",
        string platform = "",
        string organization = "",
        string project = "",
        string repository = "",
        CancellationToken ct = default)
    {
        _ = planRoot;
        if (RequiredInput.HaltIfMissing("state next-ready",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // #417 — top-level try/catch so unexpected exceptions (e.g. the
        // WorkspaceNotFoundException that wedged dispatch in cloudvault's
        // first run) surface as routable JSON with status:"error" instead
        // of a non-zero exit + stderr that the conductor gate cannot
        // route on. The verb is routing-style: every reachable path must
        // exit 0 with a structured envelope.
        try
        {
            return await NextReadyCore(
                workItem, platform, organization, project, repositoryOverride: repository, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation must surface verbatim — the host driver
            // cancels next-ready when the parent driver shuts down.
            throw;
        }
        catch (Exception ex)
        {
            EmitNextReadyError(workItem, workItemType: "", $"{ex.GetType().Name}: {ex.Message}");
            return ExitCodes.Success;
        }
    }

    private async Task<int> NextReadyCore(
        int workItem,
        string platform,
        string organization,
        string project,
        string repositoryOverride,
        CancellationToken ct)
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

        // Resolve the active RepoIdentity ONCE, with operator overrides
        // honoured first. Pass to ComputeObservedAsync so the rollup over
        // children reuses it (every sibling shares the same git repo).
        var identity = await planObserver
            .TryResolveRepoIdentityAsync(platform, organization, project, repositoryOverride, ct)
            .ConfigureAwait(false);

        var children = await repository.GetChildrenAsync(workItem, ct).ConfigureAwait(false);

        // PR #5 (apex_facets threading, closes #215): honour the
        // polyphony:facets=... tag stamped by `plan seed-children` on
        // an architect-declared indivisible apex (closed-loop §3.4 +
        // PR #214). Mirrors the helper used by EdgesCommands.Check and
        // WorklistCommands.Build so the three consumers stay on a
        // single facet-resolution path. Malformed-tag failure surfaces
        // through the existing EmitNextReadyError path rather than the
        // CollectionFailureException pattern the worklist verbs use —
        // next-ready is a routing-style verb and must always exit 0.
        IReadOnlyList<string>? overrideFacets;
        try
        {
            overrideFacets = ExtractFacetOverride(item);
        }
        catch (InvalidOperationException ex)
        {
            EmitNextReadyError(workItem, workItemType, ex.Message);
            return ExitCodes.ConfigError;
        }

        var resolved = RequirementInputResolver.Resolve(typeConfig, children.Count, overrideFacets);

        var derivation = RequirementSetDeriver.Derive(
            resolved.Facets,
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

        // Compute observable state and reduce. PR #5 adds cross-item
        // rollup over direct children inside ComputeObservedAsync (and a
        // post-reduce demotion of item_satisfied below) — both reasons
        // are surfaced via the same per-kind reasons dictionary.
        var (observed, reasons, scope) = await ComputeObservedAsync(workItem, item, children, identity, ct).ConfigureAwait(false);
        var reduced = RequirementSetReducer.Apply(derived, observed);

        // PR #5 cross-item rollup for item_satisfied: the within-item
        // reducer can promote item_satisfied to Satisfied as soon as
        // every parent facet is Satisfied, but for a parent with
        // children that is the wrong answer — the item is not done
        // until every child's item_satisfied is also Satisfied
        // (closed-loop §3.1 row 6, terminal-rollup edges in
        // CrossItemEdgeDeriver). Apply the demotion here, after the
        // reducer, instead of teaching the reducer about cross-item
        // edges — same shape PR #6's lifecycle-router will need when
        // it wires terminal kinds. Documented as an extension point
        // for PR #6 in the PR body.
        reduced = ApplyChildItemSatisfiedRollup(reduced, scope, reasons);

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

    private async Task<(ObservedRequirementState Observed, Dictionary<string, string> Reasons, NextReadyObservationScope Scope)> ComputeObservedAsync(
        int workItem,
        Twig.Domain.Aggregates.WorkItem item,
        IReadOnlyList<Twig.Domain.Aggregates.WorkItem> children,
        Polyphony.Sdlc.Observers.RepoIdentity? identity,
        CancellationToken ct)
    {
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        // ── Plan-kind observers (PR #2 — wired here). Build the per-item
        // observation scope ONCE so plan_authored / plan_reviewed /
        // plan_promoted share the underlying gh/git I/O. Sibling observers
        // (PR #3 ChildrenSeeded, PR #4 Implementation, …) plug in by
        // extending NextReadyObservationScope and adding their own composer
        // methods — see the XML doc on NextReadyObservationScope for the
        // contract. PR #5 cross-item rollup follows a slightly different
        // recipe (recursive call into THIS method for each child) — see
        // BuildChildRollupSnapshotsAsync below.
        var scope = await BuildObservationScopeAsync(workItem, item, identity, ct).ConfigureAwait(false);

        // PR #5: fetch direct-child reduced dispositions for the kinds
        // that roll up (item_satisfied, implementation_merged). Top-level
        // call uses the default depth budget; recursion below will pass a
        // decremented budget. The visited set defends against synthetic
        // cycles (well-formed parent-child trees can't cycle, but we
        // treat the data defensively — same posture as ResolveRootIdAsync).
        await BuildChildRollupSnapshotsAsync(
            scope, children, ct,
            depthRemaining: ResolveMaxRollupDepth() - 1,
            ancestors: new HashSet<int> { workItem }).ConfigureAwait(false);

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

        // ── PR #4 implementation_merged observer (per-item) + PR #5
        // cross-item rollup over implementable children. ComposeImplementationMerged
        // takes the worst-of disposition: parent's own impl PR AND every
        // implementable child's reduced impl_merged — closed-loop §3.1
        // row 5 (MG roll-up). When the parent has no implementable
        // children, the rollup is a no-op and the per-item disposition
        // stands.
        var (implMergedDisp, implMergedReason) = ComposeImplementationMerged(scope);
        reasons[RequirementKind.ImplementationMerged] = implMergedReason;

        var observed = BuildObservedFromSignals(
            planAuthoredDisp, planReviewedDisp, planPromotedDisp,
            childrenSeededDisp, implMergedDisp);
        return (observed, reasons, scope);
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
        Polyphony.Sdlc.Observers.RepoIdentity? identity,
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
            Identity = identity,
        };

        // PR #3: children_seeded depends only on the polyphony:planned tag
        // on the item itself — independent of plan branch / identity / PR
        // state. Fetch up-front so every early-return path below still
        // surfaces an observed children_seeded signal.
        await FetchPlannedTagAsync(scope, ct).ConfigureAwait(false);

        // PR #4: implementation_merged is independent of plan branch /
        // plan PR existence — fetch the impl signal up-front, before the
        // plan-branch / identity early returns below. The impl PR query
        // itself still requires the identity, so it sits AFTER identity
        // resolution but BEFORE any plan-related early returns. Empty
        // implBranch (non-positive ids) and null identity both degrade to
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

        if (scope.Identity is null)
        {
            // Without an identity we cannot list PRs; record the gap as a
            // structured fetch error so plan composers say "could not
            // resolve repo identity" rather than "no PR opened".
            scope.PlanPrFetchError = "could not resolve repo identity from origin remote";
            return scope;
        }

        try
        {
            scope.LatestPlanPr = await planObserver.GetLatestPlanPrAsync(scope.Identity, planBranch, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            scope.PlanPrFetchError = ex.FormatErrorMessage("pr list");
        }
        catch (ExternalToolException ex)
        {
            scope.PlanPrFetchError = $"pr list failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            scope.PlanPrFetchError = $"pr list failed: {ex.Message}";
        }

        if (scope.LatestPlanPr is null) return scope;

        try
        {
            scope.PlanPrPoll = await planObserver.GetPlanPrPollAsync(scope.Identity, scope.LatestPlanPr.Number, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            scope.PlanPrPollError = ex.FormatErrorMessage("pr view");
        }
        catch (ExternalToolException ex)
        {
            scope.PlanPrPollError = $"pr view failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            scope.PlanPrPollError = $"pr view failed: {ex.Message}";
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

        if (scope.Identity is null)
        {
            // Without an identity we cannot list PRs — record the gap as a
            // structured fetch error so the composer says "could not
            // resolve repo identity" rather than "no impl PR opened".
            scope.ImplPrFetchError = "could not resolve repo identity from origin remote";
            return;
        }

        try
        {
            scope.LatestImplPr = await planObserver
                .GetLatestImplPrAsync(scope.Identity, scope.ImplBranch, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            scope.ImplPrFetchError = ex.FormatErrorMessage("pr list");
            return;
        }
        catch (ExternalToolException ex)
        {
            scope.ImplPrFetchError = $"pr list failed: {ex.Message}";
            return;
        }
        catch (Exception ex)
        {
            scope.ImplPrFetchError = $"pr list failed: {ex.Message}";
            return;
        }

        if (scope.LatestImplPr is null) return;

        try
        {
            scope.ImplPrPoll = await planObserver
                .GetPlanPrPollAsync(scope.Identity, scope.LatestImplPr.Number, ct)
                .ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            scope.ImplPrPollError = ex.FormatErrorMessage("pr view");
        }
        catch (ExternalToolException ex)
        {
            scope.ImplPrPollError = $"pr view failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            scope.ImplPrPollError = $"pr view failed: {ex.Message}";
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
    /// PR #5 cross-item rollup: when <see cref="NextReadyObservationScope.ChildSnapshots"/>
    /// includes any child carrying an <c>implementation_merged</c>
    /// requirement, the parent's disposition becomes the worst-of
    /// (parent's per-item observation, every implementable child's
    /// reduced impl disposition). Closed-loop §3.1 row 5: an MG item
    /// whose own impl PR is merged but with unmerged child impl PRs is
    /// not Satisfied. Truncation (depth cap) and child-fetch errors
    /// downgrade the rollup to Needed with a structured reason rather
    /// than silently passing through the per-item disposition — silent
    /// fallback would re-introduce the lie this PR set is fixing.
    /// </remarks>
    private static (string Disposition, string Reason) ComposeImplementationMerged(NextReadyObservationScope scope)
    {
        var (selfDisposition, selfReason) = ComposeImplementationMergedSelf(scope);

        // No rollup needed when the parent has no children OR when the
        // children rollup did not run (top-level cap, fetch error). The
        // truncation/error cases still surface in the reason via the
        // dedicated branches below so the caller knows the rollup is
        // incomplete.
        if (scope.ChildRollupFetchError is not null)
        {
            return (Disposition.Needed,
                $"{selfReason}; cross-item rollup unavailable: {scope.ChildRollupFetchError}");
        }
        if (scope.ChildRollupTruncated)
        {
            return (Disposition.Needed,
                $"{selfReason}; cross-item rollup truncated at depth cap");
        }
        if (scope.ChildSnapshots is null || scope.ChildSnapshots.Count == 0)
        {
            return (selfDisposition, selfReason);
        }

        // Worst-of across implementable children only. Children without
        // an implementation_merged requirement (pure-planner types) do
        // not contribute — same posture as CrossItemEdgeDeriver's
        // terminal-rollup which only emits edges for kinds that exist
        // on both endpoints.
        var worstDisposition = selfDisposition;
        ChildRollupSnapshot? worstChild = null;
        var anyImplementableChild = false;
        foreach (var snap in scope.ChildSnapshots)
        {
            if (snap.Error is not null)
            {
                // Treat error as worst-case — same posture as fetch
                // errors above. Surface the child id in the reason so
                // the caller can drill down.
                return (Disposition.Needed,
                    $"{selfReason}; child #{snap.ChildId} rollup error: {snap.Error}");
            }
            if (!snap.HasImplementationMergedKind) continue;
            anyImplementableChild = true;
            if (Disposition.Order(snap.ImplementationMergedDisposition) < Disposition.Order(worstDisposition))
            {
                worstDisposition = snap.ImplementationMergedDisposition;
                worstChild = snap;
            }
        }

        if (!anyImplementableChild || worstChild is null)
        {
            // No child weakened the disposition — pass through.
            return (selfDisposition, selfReason);
        }

        var rolledReason = worstDisposition switch
        {
            Disposition.Needed => $"{selfReason}; child #{worstChild.ChildId} impl needed",
            Disposition.Ready => $"{selfReason}; child #{worstChild.ChildId} impl ready (no PR)",
            Disposition.Fulfilling => $"{selfReason}; child #{worstChild.ChildId} impl fulfilling",
            _ => $"{selfReason}; child #{worstChild.ChildId} impl {worstDisposition}",
        };
        return (worstDisposition, rolledReason);
    }

    /// <summary>
    /// PR #4's per-item composer extracted out so PR #5's
    /// <see cref="ComposeImplementationMerged"/> can call it to compute
    /// the parent's own disposition before rolling up children.
    /// </summary>
    private static (string Disposition, string Reason) ComposeImplementationMergedSelf(NextReadyObservationScope scope)
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

    // ── PR #5 cross-item rollup ────────────────────────────────────────

    /// <summary>Default cap on the recursive depth used by
    /// <see cref="BuildChildRollupSnapshotsAsync"/>. Overridable via the
    /// <c>POLYPHONY_NEXTREADY_ROLLUP_DEPTH</c> environment variable.
    /// Five levels is enough for every tree shape the dogfood corpus has
    /// produced (apex → MG → leaves is depth 3; the cap leaves headroom
    /// for nested MGs without exposing the verb to runaway gh calls on
    /// adversarial fixtures). When the cap is reached at a parent that
    /// still has children, the parent's
    /// <see cref="NextReadyObservationScope.ChildRollupTruncated"/> flag
    /// is set and downstream composers degrade to Needed with a
    /// "rollup truncated" reason rather than silently passing through
    /// the per-item disposition.</summary>
    private const int DefaultMaxRollupDepth = 5;

    private static int ResolveMaxRollupDepth()
    {
        var raw = Environment.GetEnvironmentVariable("POLYPHONY_NEXTREADY_ROLLUP_DEPTH");
        if (int.TryParse(raw, out var v) && v > 0 && v <= 64) return v;
        return DefaultMaxRollupDepth;
    }

    /// <summary>
    /// PR #5 cross-item rollup: for each direct child of the parent
    /// item, recursively run the same per-item observation pipeline and
    /// reduce the resulting requirement set. The reduced
    /// <c>item_satisfied</c> and <c>implementation_merged</c>
    /// dispositions are captured into a <see cref="ChildRollupSnapshot"/>
    /// and stashed on the parent's
    /// <see cref="NextReadyObservationScope.ChildSnapshots"/> for the
    /// composers / post-reduce demotion to consume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three failure modes that must never escape:
    /// </para>
    /// <list type="number">
    ///   <item><description><b>Cycle</b>: a child's id appears in
    ///     <paramref name="ancestors"/>. Real ADO trees can't cycle
    ///     (parent_id is single-valued), but seeded fixtures can; we
    ///     defend by recording the cycle on the snapshot and
    ///     short-circuiting the recursion.</description></item>
    ///   <item><description><b>Depth cap</b>: <paramref name="depthRemaining"/>
    ///     is zero on entry to a non-empty children list. Mark
    ///     <see cref="NextReadyObservationScope.ChildRollupTruncated"/>
    ///     on the parent and return without recursing — the
    ///     impl/item_satisfied composers degrade to Needed with a
    ///     structured reason.</description></item>
    ///   <item><description><b>Per-child fault</b>: type missing,
    ///     derivation invalid, observation throws — captured into the
    ///     snapshot's <see cref="ChildRollupSnapshot.Error"/> and
    ///     surfaced as the worst-case disposition for that child.</description></item>
    /// </list>
    /// <para>
    /// Children are observed in parallel via <see cref="Task.WhenAll(IEnumerable{Task})"/>
    /// — each child holds an independent set of gh / git / twig
    /// shell-outs, no shared mutable state. The visited set is cloned
    /// per-branch so concurrent recursion can't race; the trade-off
    /// (sibling subtrees that re-observe the same descendant) is
    /// acceptable because real trees don't share descendants across
    /// siblings (parent_id is single-valued).
    /// </para>
    /// </remarks>
    private async Task BuildChildRollupSnapshotsAsync(
        NextReadyObservationScope parentScope,
        IReadOnlyList<Twig.Domain.Aggregates.WorkItem> children,
        CancellationToken ct,
        int depthRemaining,
        IReadOnlySet<int> ancestors)
    {
        if (children.Count == 0)
        {
            parentScope.ChildSnapshots = Array.Empty<ChildRollupSnapshot>();
            return;
        }
        if (depthRemaining < 0)
        {
            parentScope.ChildRollupTruncated = true;
            return;
        }

        var ordered = children.OrderBy(c => c.Id).ToArray();
        var tasks = ordered
            .Select(child => ComputeChildSnapshotAsync(child, parentScope.Identity, ct, depthRemaining, ancestors))
            .ToArray();

        ChildRollupSnapshot[] snapshots;
        try
        {
            snapshots = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ComputeChildSnapshotAsync swallows internally — any escape
            // here is a bug, but keep the verb's exit-0 contract intact.
            parentScope.ChildRollupFetchError = $"child rollup faulted: {ex.Message}";
            return;
        }
        parentScope.ChildSnapshots = snapshots;
    }

    private async Task<ChildRollupSnapshot> ComputeChildSnapshotAsync(
        Twig.Domain.Aggregates.WorkItem child,
        Polyphony.Sdlc.Observers.RepoIdentity? identity,
        CancellationToken ct,
        int depthRemaining,
        IReadOnlySet<int> ancestors)
    {
        if (ancestors.Contains(child.Id))
        {
            return new ChildRollupSnapshot
            {
                ChildId = child.Id,
                ItemSatisfiedDisposition = Disposition.Needed,
                ImplementationMergedDisposition = Disposition.Needed,
                HasImplementationMergedKind = false,
                Error = $"cycle detected — child #{child.Id} also appears as an ancestor",
            };
        }

        try
        {
            var typeName = child.Type.Value ?? "";
            if (string.IsNullOrEmpty(typeName) || !processConfig.Types.TryGetValue(typeName, out var typeConfig))
            {
                return new ChildRollupSnapshot
                {
                    ChildId = child.Id,
                    ItemSatisfiedDisposition = Disposition.Needed,
                    ImplementationMergedDisposition = Disposition.Needed,
                    HasImplementationMergedKind = false,
                    Error = $"type '{typeName}' not found in process config",
                };
            }

            var grandchildren = await repository.GetChildrenAsync(child.Id, ct).ConfigureAwait(false);

            IReadOnlyList<string>? overrideFacets;
            try
            {
                overrideFacets = ExtractFacetOverride(child);
            }
            catch (InvalidOperationException ex)
            {
                return new ChildRollupSnapshot
                {
                    ChildId = child.Id,
                    ItemSatisfiedDisposition = Disposition.Needed,
                    ImplementationMergedDisposition = Disposition.Needed,
                    HasImplementationMergedKind = false,
                    Error = ex.Message,
                };
            }

            var resolved = RequirementInputResolver.Resolve(typeConfig, grandchildren.Count, overrideFacets);
            var derivation = RequirementSetDeriver.Derive(
                resolved.Facets,
                resolved.Decomposable,
                resolved.FacetOrder,
                resolved.ActionableExecutor);

            if (!derivation.IsValid || derivation.Set is null)
            {
                return new ChildRollupSnapshot
                {
                    ChildId = child.Id,
                    ItemSatisfiedDisposition = Disposition.Needed,
                    ImplementationMergedDisposition = Disposition.Needed,
                    HasImplementationMergedKind = false,
                    Error = "derivation failed: " + string.Join("; ", derivation.Errors),
                };
            }

            var derived = derivation.Set;

            // Pure container: no own-work requirements. Treat as
            // Satisfied for rollup purposes — there is nothing the
            // child can fail at. Mirrors the "empty" status path in
            // NextReady.
            if (derived.Items.Count == 0)
            {
                return new ChildRollupSnapshot
                {
                    ChildId = child.Id,
                    ItemSatisfiedDisposition = Disposition.Satisfied,
                    ImplementationMergedDisposition = Disposition.Satisfied,
                    HasImplementationMergedKind = false,
                };
            }

            // Recurse: build child observation, including grandchild
            // rollup if depth permits.
            var childScope = await BuildObservationScopeAsync(child.Id, child, identity, ct).ConfigureAwait(false);
            var nextAncestors = new HashSet<int>(ancestors) { child.Id };
            await BuildChildRollupSnapshotsAsync(
                childScope, grandchildren, ct,
                depthRemaining: depthRemaining - 1,
                ancestors: nextAncestors).ConfigureAwait(false);

            var (planAuthoredDisp, _) = ComposePlanAuthored(childScope);
            var (planReviewedDisp, _) = ComposePlanReviewed(childScope);
            var (planPromotedDisp, _) = ComposePlanPromoted(childScope);
            var (childrenSeededDisp, _) = ComposeChildrenSeeded(childScope);
            var (implMergedDisp, _) = ComposeImplementationMerged(childScope);

            var childObserved = BuildObservedFromSignals(
                planAuthoredDisp, planReviewedDisp, planPromotedDisp,
                childrenSeededDisp, implMergedDisp);
            var childReduced = RequirementSetReducer.Apply(derived, childObserved);

            // Mirror the post-reduce demotion the parent applies to
            // itself — without this, a non-leaf child with grandchildren
            // would report Satisfied for item_satisfied as soon as its
            // own facets were Satisfied, even if its grandchildren were
            // still in flight (the same closed-loop bug, one level down).
            var dummyReasons = new Dictionary<string, string>(StringComparer.Ordinal);
            childReduced = ApplyChildItemSatisfiedRollup(childReduced, childScope, dummyReasons);

            var itemSatisfiedDisp = FindDisposition(childReduced, RequirementKind.ItemSatisfied)
                ?? Disposition.Needed;
            var implReduced = FindDisposition(childReduced, RequirementKind.ImplementationMerged);

            return new ChildRollupSnapshot
            {
                ChildId = child.Id,
                ItemSatisfiedDisposition = itemSatisfiedDisp,
                ImplementationMergedDisposition = implReduced ?? Disposition.Needed,
                HasImplementationMergedKind = implReduced is not null,
            };
        }
        catch (Exception ex)
        {
            return new ChildRollupSnapshot
            {
                ChildId = child.Id,
                ItemSatisfiedDisposition = Disposition.Needed,
                ImplementationMergedDisposition = Disposition.Needed,
                HasImplementationMergedKind = false,
                Error = $"child observation failed: {ex.Message}",
            };
        }
    }

    private static string? FindDisposition(RequirementSet set, string kind)
    {
        foreach (var req in set.Items)
        {
            if (string.Equals(req.Kind, kind, StringComparison.Ordinal)) return req.Disposition;
        }
        return null;
    }

    /// <summary>
    /// PR #5 post-reduce demotion of <c>item_satisfied</c> based on
    /// direct children's reduced <c>item_satisfied</c> dispositions.
    /// Implements the terminal-rollup edge from
    /// <see cref="CrossItemEdgeDeriver"/> — the within-item reducer
    /// only knows about within-item edges, so we fold the cross-item
    /// signal in here. The reason for the demotion is appended to the
    /// per-kind reasons dictionary so the envelope shows why the
    /// disposition is below what the within-item reducer alone would
    /// have produced.
    /// </summary>
    /// <remarks>
    /// Truncation and child-fetch errors at the parent level demote
    /// <c>item_satisfied</c> to Needed unconditionally — silent
    /// fallback would re-introduce the lie this PR set is fixing.
    /// </remarks>
    private static RequirementSet ApplyChildItemSatisfiedRollup(
        RequirementSet reduced,
        NextReadyObservationScope scope,
        IDictionary<string, string> reasons)
    {
        var idx = -1;
        for (var i = 0; i < reduced.Items.Count; i++)
        {
            if (string.Equals(reduced.Items[i].Kind, RequirementKind.ItemSatisfied, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return reduced;

        var current = reduced.Items[idx].Disposition;

        string? newDisposition = null;
        string? rollupReason = null;

        if (scope.ChildRollupFetchError is not null)
        {
            newDisposition = Disposition.Needed;
            rollupReason = $"cross-item rollup unavailable: {scope.ChildRollupFetchError}";
        }
        else if (scope.ChildRollupTruncated)
        {
            newDisposition = Disposition.Needed;
            rollupReason = "cross-item rollup truncated at depth cap";
        }
        else if (scope.ChildSnapshots is { Count: > 0 } snaps)
        {
            var worst = current;
            ChildRollupSnapshot? worstChild = null;
            foreach (var snap in snaps)
            {
                if (snap.Error is not null)
                {
                    newDisposition = Disposition.Needed;
                    rollupReason = $"child #{snap.ChildId} rollup error: {snap.Error}";
                    worstChild = snap;
                    break;
                }
                if (Disposition.Order(snap.ItemSatisfiedDisposition) < Disposition.Order(worst))
                {
                    worst = snap.ItemSatisfiedDisposition;
                    worstChild = snap;
                }
            }
            if (newDisposition is null && worstChild is not null && !string.Equals(worst, current, StringComparison.Ordinal))
            {
                newDisposition = worst;
                rollupReason = worst switch
                {
                    Disposition.Needed => $"blocked on child #{worstChild.ChildId} item_satisfied=needed",
                    Disposition.Fulfilling => $"awaiting child #{worstChild.ChildId} item_satisfied=fulfilling",
                    Disposition.Ready => $"awaiting child #{worstChild.ChildId} item_satisfied=ready",
                    _ => $"rolled up child #{worstChild.ChildId} item_satisfied={worst}",
                };
            }
        }

        if (newDisposition is null) return reduced;

        var items = reduced.Items.ToArray();
        items[idx] = items[idx] with { Disposition = newDisposition };
        if (rollupReason is not null)
        {
            reasons[RequirementKind.ItemSatisfied] = rollupReason;
        }
        return new RequirementSet(items, reduced.Edges);
    }

    /// <summary>
    /// Mirror of the <c>ExtractFacetOverride</c> helper used by
    /// <see cref="EdgesCommands"/> and <see cref="WorklistCommands"/>:
    /// pulls the per-item facet override from the
    /// <c>polyphony:facets=...</c> tag stamped by
    /// <c>plan seed-children</c> for an indivisible apex
    /// (closed-loop §3.4 + PR #214). Returns <c>null</c> when no
    /// override tag is present (the resolver then falls back to the
    /// type-config default). Throws <see cref="InvalidOperationException"/>
    /// on a malformed override — silent fallback would route the apex
    /// to the wrong facet set, the exact failure mode this PR set is
    /// fixing.
    /// </summary>
    /// <remarks>
    /// Duplicating the helper across three verbs is intentional for now
    /// — pulling it into a shared location couples the routing-style
    /// (next-ready, exit 0 with envelope) and worklist-style (build,
    /// throw <c>CollectionFailureException</c>) error postures. When
    /// (and only when) a fourth consumer needs the same logic, factor
    /// it into <c>Polyphony.Sdlc</c> with explicit error-routing
    /// callbacks.
    /// </remarks>
    private static IReadOnlyList<string>? ExtractFacetOverride(Twig.Domain.Aggregates.WorkItem item)
    {
        item.Fields.TryGetValue("System.Tags", out var raw);
        var tags = TagSet.Parse(raw);
        var parsed = FacetTagParser.TryExtract(tags);
        if (parsed is null) return null;
        if (!parsed.IsValid)
        {
            throw new InvalidOperationException(
                $"Item {item.Id} has malformed polyphony:facets tag — unknown facet(s): {string.Join(", ", parsed.UnknownFacets)}.");
        }
        return parsed.Facets.Count == 0 ? null : parsed.Facets;
    }
}
