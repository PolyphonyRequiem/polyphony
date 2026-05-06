using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Exhaustive tests for <see cref="RequirementSetDeriver.Derive"/>. Covers
/// every combination of facets × decomposable × facet_order × executor that
/// downstream consumers will see, plus all validation error paths.
/// </summary>
/// <remarks>
/// Pattern: structural assertions, not snapshots. Each test verifies the
/// presence or absence of specific requirement kinds and edges; collection
/// shape (not Verify-style golden files). Keeps tests robust against
/// reordering changes that don't affect semantics.
/// </remarks>
public sealed class RequirementSetDeriverTests
{
    // ── Validation: invalid inputs ───────────────────────────────────────

    [Fact]
    public void Derive_EmptyFacetsAndNotDecomposable_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(facets: [], decomposable: false);

        result.IsValid.ShouldBeFalse();
        result.Set.ShouldBeNull();
        result.Errors.ShouldContain(e => e.Contains("must be decomposable"));
    }

    [Fact]
    public void Derive_UnknownFacet_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(["plannable", "deployable"], decomposable: true);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Unknown facet 'deployable'"));
    }

    [Fact]
    public void Derive_DuplicateFacet_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(["plannable", "plannable"], decomposable: true);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Duplicate facet 'plannable'"));
    }

    [Fact]
    public void Derive_ActionableWithoutExecutor_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(["actionable"], decomposable: false);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("requires an executor"));
    }

    [Fact]
    public void Derive_ExecutorWithoutActionable_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(
            ["implementable"], decomposable: false, actionableExecutor: "polyphony");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Actionable executor supplied but item is not actionable"));
    }

    [Fact]
    public void Derive_UnknownExecutor_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable"], decomposable: false, actionableExecutor: "agent");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Unknown actionable executor 'agent'"));
    }

    [Fact]
    public void Derive_ActionableAndImplementableWithoutFacetOrder_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable", "implementable"], decomposable: false, actionableExecutor: "human");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("facet_order is required"));
    }

    [Fact]
    public void Derive_FacetOrderWithUnknownFacet_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable", "implementable"],
            decomposable: false,
            facetOrder: ["actionable", "deployable"],
            actionableExecutor: "human");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("unknown facet 'deployable'"));
    }

    [Fact]
    public void Derive_FacetOrderMissingPresentFacet_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable", "implementable"],
            decomposable: false,
            facetOrder: ["actionable"],
            actionableExecutor: "human");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("must include both"));
    }

    [Fact]
    public void Derive_FacetOrderReferencesAbsentFacet_IsInvalid()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable", "implementable"],
            decomposable: false,
            facetOrder: ["actionable", "implementable", "plannable"],
            actionableExecutor: "human");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("not in the item's facet set"));
    }

    [Fact]
    public void Derive_FacetOrderForSingleNonPlannableFacet_EmitsWarning()
    {
        var result = RequirementSetDeriver.Derive(
            ["implementable"],
            decomposable: false,
            facetOrder: ["implementable"]);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("ordering is unambiguous"));
    }

    // ── Pure container: empty facet set + decomposable=true ──────────────

    [Fact]
    public void Derive_PureContainer_HasNoOwnRequirements()
    {
        var result = RequirementSetDeriver.Derive(facets: [], decomposable: true);

        result.IsValid.ShouldBeTrue();
        result.Set.ShouldNotBeNull();
        result.Set!.Items.ShouldBeEmpty();
        result.Set.Edges.ShouldBeEmpty();
    }

    // ── Plannable only ───────────────────────────────────────────────────

    [Fact]
    public void Derive_PlannableLeaf_EmitsThreePlanRequirementsAndIntraFacetEdges()
    {
        var result = RequirementSetDeriver.Derive(["plannable"], decomposable: false);

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set, RequirementKind.PlanAuthored, RequirementKind.PlanReviewed, RequirementKind.PlanPromoted);
        AssertNoKind(set, RequirementKind.ChildrenSeeded);
        AssertEdge(set, RequirementKind.PlanAuthored, RequirementKind.PlanReviewed);
        AssertEdge(set, RequirementKind.PlanReviewed, RequirementKind.PlanPromoted);
    }

    [Fact]
    public void Derive_PlannableDecomposable_AddsChildrenSeededRequirement()
    {
        var result = RequirementSetDeriver.Derive(["plannable"], decomposable: true);

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set, RequirementKind.PlanAuthored, RequirementKind.PlanReviewed,
            RequirementKind.PlanPromoted, RequirementKind.ChildrenSeeded);
        AssertEdge(set, RequirementKind.PlanPromoted, RequirementKind.ChildrenSeeded);
    }

    // ── Implementable only ───────────────────────────────────────────────

    [Fact]
    public void Derive_ImplementableLeaf_EmitsImplementationMergedOnly()
    {
        var result = RequirementSetDeriver.Derive(["implementable"], decomposable: false);

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set, RequirementKind.ImplementationMerged);
        set.Items.Count.ShouldBe(1);
        set.Edges.ShouldBeEmpty();
    }

    // ── Actionable only ──────────────────────────────────────────────────

    [Fact]
    public void Derive_ActionablePolyphony_EmitsActionAndEvidence()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable"], decomposable: false, actionableExecutor: "polyphony");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set, RequirementKind.ActionSatisfied, RequirementKind.EvidenceAccepted);
        AssertEdge(set, RequirementKind.ActionSatisfied, RequirementKind.EvidenceAccepted);
    }

    [Fact]
    public void Derive_ActionableHuman_EmitsActionWithoutEvidence()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable"], decomposable: false, actionableExecutor: "human");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set, RequirementKind.ActionSatisfied);
        AssertNoKind(set, RequirementKind.EvidenceAccepted);
        set.Edges.ShouldBeEmpty();
    }

    // ── Plannable + implementable ────────────────────────────────────────

    [Fact]
    public void Derive_PlannableImplementableLeaf_GatesImplementationOnPlanPromoted()
    {
        var result = RequirementSetDeriver.Derive(
            ["plannable", "implementable"], decomposable: false);

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set, RequirementKind.PlanAuthored, RequirementKind.PlanReviewed,
            RequirementKind.PlanPromoted, RequirementKind.ImplementationMerged);
        AssertNoKind(set, RequirementKind.ChildrenSeeded);
        AssertEdge(set, RequirementKind.PlanPromoted, RequirementKind.ImplementationMerged);
    }

    [Fact]
    public void Derive_PlannableDecomposableImplementable_GatesImplementationOnChildrenSeeded()
    {
        var result = RequirementSetDeriver.Derive(
            ["plannable", "implementable"], decomposable: true);

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set, RequirementKind.PlanAuthored, RequirementKind.PlanReviewed,
            RequirementKind.PlanPromoted, RequirementKind.ChildrenSeeded,
            RequirementKind.ImplementationMerged);
        AssertEdge(set, RequirementKind.ChildrenSeeded, RequirementKind.ImplementationMerged);
        // The plan→children gate is still present.
        AssertEdge(set, RequirementKind.PlanPromoted, RequirementKind.ChildrenSeeded);
        // No direct plan_promoted → implementation_merged when children_seeded interposes.
        AssertNoEdge(set, RequirementKind.PlanPromoted, RequirementKind.ImplementationMerged);
    }

    // ── Plannable + actionable ───────────────────────────────────────────

    [Fact]
    public void Derive_PlannableActionablePolyphony_GatesActionOnPlanPromoted()
    {
        var result = RequirementSetDeriver.Derive(
            ["plannable", "actionable"], decomposable: false, actionableExecutor: "polyphony");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set, RequirementKind.PlanAuthored, RequirementKind.PlanReviewed,
            RequirementKind.PlanPromoted, RequirementKind.ActionSatisfied,
            RequirementKind.EvidenceAccepted);
        AssertEdge(set, RequirementKind.PlanPromoted, RequirementKind.ActionSatisfied);
        AssertEdge(set, RequirementKind.ActionSatisfied, RequirementKind.EvidenceAccepted);
    }

    // ── Actionable + implementable: facet_order matters ──────────────────

    [Fact]
    public void Derive_ActionableImplementable_FacetOrderActionFirst_GatesImplementationOnAction()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable", "implementable"],
            decomposable: false,
            facetOrder: ["actionable", "implementable"],
            actionableExecutor: "polyphony");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertEdge(set, RequirementKind.ActionSatisfied, RequirementKind.ImplementationMerged);
        AssertNoEdge(set, RequirementKind.ImplementationMerged, RequirementKind.ActionSatisfied);
    }

    [Fact]
    public void Derive_ActionableImplementable_FacetOrderImplementationFirst_GatesActionOnImplementation()
    {
        var result = RequirementSetDeriver.Derive(
            ["actionable", "implementable"],
            decomposable: false,
            facetOrder: ["implementable", "actionable"],
            actionableExecutor: "human");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertEdge(set, RequirementKind.ImplementationMerged, RequirementKind.ActionSatisfied);
        AssertNoEdge(set, RequirementKind.ActionSatisfied, RequirementKind.ImplementationMerged);
    }

    // ── All three facets ─────────────────────────────────────────────────

    [Fact]
    public void Derive_AllThreeFacets_PlannableFirst_ActionFirstOfRest()
    {
        var result = RequirementSetDeriver.Derive(
            ["plannable", "actionable", "implementable"],
            decomposable: true,
            facetOrder: ["actionable", "implementable"],
            actionableExecutor: "polyphony");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        AssertHasKinds(set,
            RequirementKind.PlanAuthored, RequirementKind.PlanReviewed, RequirementKind.PlanPromoted,
            RequirementKind.ChildrenSeeded,
            RequirementKind.ActionSatisfied, RequirementKind.EvidenceAccepted,
            RequirementKind.ImplementationMerged);

        // Plan → children seed (definitional)
        AssertEdge(set, RequirementKind.PlanPromoted, RequirementKind.ChildrenSeeded);
        // Plan family → first non-plannable (action)
        AssertEdge(set, RequirementKind.ChildrenSeeded, RequirementKind.ActionSatisfied);
        // Action → implementation (facet_order)
        AssertEdge(set, RequirementKind.ActionSatisfied, RequirementKind.ImplementationMerged);
        // Action → evidence
        AssertEdge(set, RequirementKind.ActionSatisfied, RequirementKind.EvidenceAccepted);
    }

    [Fact]
    public void Derive_AllThreeFacets_ImplementationFirstOfRest()
    {
        var result = RequirementSetDeriver.Derive(
            ["plannable", "actionable", "implementable"],
            decomposable: false,
            facetOrder: ["implementable", "actionable"],
            actionableExecutor: "human");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        // Plan → first non-plannable (implementation)
        AssertEdge(set, RequirementKind.PlanPromoted, RequirementKind.ImplementationMerged);
        // Implementation → action (facet_order)
        AssertEdge(set, RequirementKind.ImplementationMerged, RequirementKind.ActionSatisfied);
        // Human executor: no evidence requirement.
        AssertNoKind(set, RequirementKind.EvidenceAccepted);
    }

    // ── Disposition + provenance invariants ──────────────────────────────

    [Fact]
    public void Derive_AllRequirementsStartNeeded()
    {
        var result = RequirementSetDeriver.Derive(
            ["plannable", "actionable", "implementable"],
            decomposable: true,
            facetOrder: ["actionable", "implementable"],
            actionableExecutor: "polyphony");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        foreach (var req in set.Items)
        {
            req.Disposition.ShouldBe(Disposition.Needed);
            req.AcceptanceCriteria.ShouldBeNull();
        }
    }

    [Fact]
    public void Derive_AllEdgesCarryDefinitionalSourceAndSatisfiedThreshold()
    {
        var result = RequirementSetDeriver.Derive(
            ["plannable", "actionable", "implementable"],
            decomposable: true,
            facetOrder: ["actionable", "implementable"],
            actionableExecutor: "polyphony");

        result.IsValid.ShouldBeTrue();
        var set = result.Set.ShouldNotBeNull();
        foreach (var edge in set.Edges)
        {
            edge.Source.ShouldBe(RequirementEdgeSource.Definitional);
            edge.RequiredDisposition.ShouldBe(Disposition.Satisfied);
        }
    }

    // ── Theory: every facet subset is decomposable-or-handled ────────────

    /// <summary>Cartesian sweep that the deriver returns SOMETHING (success or
    /// well-formed validation failure) for every reachable input combo.</summary>
    [Theory]
    [MemberData(nameof(EveryReachableCombo))]
    public void Derive_EveryReachableCombo_ReturnsWellFormedResult(
        string[] facets, bool decomposable, string[]? facetOrder, string? executor)
    {
        var result = RequirementSetDeriver.Derive(facets, decomposable, facetOrder, executor);

        // Either success OR a non-empty error list — never both, never neither.
        if (result.IsValid)
        {
            result.Set.ShouldNotBeNull();
            result.Errors.ShouldBeEmpty();
        }
        else
        {
            result.Set.ShouldBeNull();
            result.Errors.ShouldNotBeEmpty();
        }
    }

    public static IEnumerable<object?[]> EveryReachableCombo()
    {
        var facetOptions = new[]
        {
            Array.Empty<string>(),
            new[] { "plannable" },
            new[] { "actionable" },
            new[] { "implementable" },
            new[] { "plannable", "actionable" },
            new[] { "plannable", "implementable" },
            new[] { "actionable", "implementable" },
            new[] { "plannable", "actionable", "implementable" },
        };
        var decomposableOptions = new[] { false, true };
        var orderOptions = new string[]?[]
        {
            null,
            ["actionable", "implementable"],
            ["implementable", "actionable"],
        };
        var executorOptions = new string?[] { null, "polyphony", "human" };

        foreach (var f in facetOptions)
        foreach (var d in decomposableOptions)
        foreach (var o in orderOptions)
        foreach (var e in executorOptions)
        {
            yield return [f, d, o, e];
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void AssertHasKinds(RequirementSet set, params string[] kinds)
    {
        var have = set.Items.Select(r => r.Kind).ToHashSet();
        foreach (var k in kinds)
        {
            have.ShouldContain(k, $"requirement '{k}' missing from set");
        }
    }

    private static void AssertNoKind(RequirementSet set, string kind)
    {
        set.Items.ShouldNotContain(r => r.Kind == kind, $"unexpected requirement '{kind}' in set");
    }

    private static void AssertEdge(RequirementSet set, string from, string to)
    {
        set.Edges.ShouldContain(
            e => e.PrerequisiteKind == from && e.DependentKind == to,
            $"edge '{from} → {to}' missing");
    }

    private static void AssertNoEdge(RequirementSet set, string from, string to)
    {
        set.Edges.ShouldNotContain(
            e => e.PrerequisiteKind == from && e.DependentKind == to,
            $"unexpected edge '{from} → {to}' in set");
    }
}
