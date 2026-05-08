using Polyphony.Configuration;
using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for <see cref="RequirementInputResolver.Resolve"/>. The resolver
/// bridges <see cref="TypeConfig"/> + observable signals to the deriver inputs;
/// the priority chain for <c>decomposable</c> inference is the highest-risk
/// area and is covered exhaustively here.
/// </summary>
public sealed class RequirementInputResolverTests
{
    private static TypeConfig Type(
        bool? decomposable = null,
        string[]? facetOrder = null,
        string? actionableExecutor = null,
        string[]? allowedChildTypes = null,
        string? decompositionGuidance = null,
        string[]? facets = null,
        string? executionMode = null) => new()
    {
        Decomposable = decomposable,
        FacetOrder = facetOrder,
        ActionableExecutor = actionableExecutor,
        AllowedChildTypes = allowedChildTypes ?? [],
        DecompositionGuidance = decompositionGuidance,
        Facets = facets ?? [],
        ExecutionMode = executionMode,
    };

    // ── decomposable: explicit wins ─────────────────────────────────────

    [Fact]
    public void Resolve_DecomposableExplicitTrue_ReturnsTrueWithExplicitProvenance()
    {
        var resolved = RequirementInputResolver.Resolve(Type(decomposable: true), childCount: 0);

        resolved.Decomposable.ShouldBeTrue();
        resolved.DecomposableProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_DecomposableExplicitFalse_ReturnsFalseEvenWithChildren()
    {
        // Explicit always wins — even when observable signals would suggest otherwise.
        var resolved = RequirementInputResolver.Resolve(
            Type(decomposable: false, allowedChildTypes: ["Task"], decompositionGuidance: "split"),
            childCount: 5);

        resolved.Decomposable.ShouldBeFalse();
        resolved.DecomposableProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    // ── decomposable: inference chain ───────────────────────────────────

    [Fact]
    public void Resolve_DecomposableUnset_ChildrenPresent_InfersTrue()
    {
        var resolved = RequirementInputResolver.Resolve(Type(), childCount: 1);

        resolved.Decomposable.ShouldBeTrue();
        resolved.DecomposableProvenance.ShouldBe(ResolutionProvenance.Inferred);
    }

    [Fact]
    public void Resolve_DecomposableUnset_NoChildren_AllowedChildTypesNonEmpty_InfersTrue()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(allowedChildTypes: ["Task"]),
            childCount: 0);

        resolved.Decomposable.ShouldBeTrue();
        resolved.DecomposableProvenance.ShouldBe(ResolutionProvenance.Inferred);
    }

    [Fact]
    public void Resolve_DecomposableUnset_OnlyDecompositionGuidance_InfersTrue()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(decompositionGuidance: "split into smaller pieces"),
            childCount: 0);

        resolved.Decomposable.ShouldBeTrue();
        resolved.DecomposableProvenance.ShouldBe(ResolutionProvenance.Inferred);
    }

    [Fact]
    public void Resolve_DecomposableUnset_NoSignals_InfersFalse()
    {
        var resolved = RequirementInputResolver.Resolve(Type(), childCount: 0);

        resolved.Decomposable.ShouldBeFalse();
        resolved.DecomposableProvenance.ShouldBe(ResolutionProvenance.Inferred);
    }

    [Fact]
    public void Resolve_DecomposableUnset_WhitespaceOnlyGuidance_DoesNotInferTrue()
    {
        // IsNullOrWhiteSpace check guards against inadvertent placeholder values.
        var resolved = RequirementInputResolver.Resolve(
            Type(decompositionGuidance: "   "),
            childCount: 0);

        resolved.Decomposable.ShouldBeFalse();
    }

    // ── facet_order ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_FacetOrderExplicit_ReturnsExplicitProvenance()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(facetOrder: ["actionable", "implementable"]),
            childCount: 0);

        resolved.FacetOrder.ShouldBe(["actionable", "implementable"]);
        resolved.FacetOrderProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_FacetOrderUnset_ReturnsNotApplicableProvenance()
    {
        var resolved = RequirementInputResolver.Resolve(Type(), childCount: 0);

        resolved.FacetOrder.ShouldBeNull();
        resolved.FacetOrderProvenance.ShouldBe(ResolutionProvenance.NotApplicable);
    }

    [Fact]
    public void Resolve_FacetOrderEmpty_TreatedAsNotApplicable()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(facetOrder: []),
            childCount: 0);

        resolved.FacetOrderProvenance.ShouldBe(ResolutionProvenance.NotApplicable);
    }

    // ── actionable_executor ─────────────────────────────────────────────

    [Fact]
    public void Resolve_ActionableExecutorExplicit_ReturnsExplicitProvenance()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(actionableExecutor: "polyphony"),
            childCount: 0);

        resolved.ActionableExecutor.ShouldBe("polyphony");
        resolved.ActionableExecutorProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_ActionableExecutorUnset_ReturnsNotApplicableProvenance()
    {
        var resolved = RequirementInputResolver.Resolve(Type(), childCount: 0);

        resolved.ActionableExecutor.ShouldBeNull();
        resolved.ActionableExecutorProvenance.ShouldBe(ResolutionProvenance.NotApplicable);
    }

    // ── AnyInferred ─────────────────────────────────────────────────────

    [Fact]
    public void AnyInferred_TrueWhenDecomposableInferred()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(allowedChildTypes: ["Task"]),
            childCount: 0);

        resolved.AnyInferred.ShouldBeTrue();
    }

    [Fact]
    public void AnyInferred_FalseWhenAllExplicitOrNotApplicable()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(decomposable: true),
            childCount: 0);

        // decomposable=Explicit; facet_order/executor=NotApplicable → not inferred.
        resolved.AnyInferred.ShouldBeFalse();
    }

    // ── argument validation ─────────────────────────────────────────────

    [Fact]
    public void Resolve_NullType_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            RequirementInputResolver.Resolve(null!, childCount: 0));
    }

    // ── execution_mode ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_ExecutionModeUnset_DefaultsToParallel()
    {
        var resolved = RequirementInputResolver.Resolve(Type(), childCount: 0);

        resolved.ExecutionMode.ShouldBe(ExecutionMode.Parallel);
        resolved.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Default);
    }

    [Fact]
    public void Resolve_ExecutionModeEmpty_DefaultsToParallel()
    {
        var resolved = RequirementInputResolver.Resolve(Type(executionMode: ""), childCount: 0);

        resolved.ExecutionMode.ShouldBe(ExecutionMode.Parallel);
        resolved.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Default);
    }

    [Fact]
    public void Resolve_ExecutionModeWhitespace_DefaultsToParallel()
    {
        var resolved = RequirementInputResolver.Resolve(Type(executionMode: "   "), childCount: 0);

        resolved.ExecutionMode.ShouldBe(ExecutionMode.Parallel);
        resolved.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Default);
    }

    [Fact]
    public void Resolve_ExecutionModeConfiguredParallel_PassesThroughExplicit()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(executionMode: ExecutionMode.Parallel), childCount: 0);

        resolved.ExecutionMode.ShouldBe(ExecutionMode.Parallel);
        resolved.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_ExecutionModeConfiguredPlanThenImplement_PassesThroughExplicit()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(executionMode: ExecutionMode.PlanThenImplement), childCount: 0);

        resolved.ExecutionMode.ShouldBe(ExecutionMode.PlanThenImplement);
        resolved.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_ExecutionModeUnknown_PassesThroughExplicit()
    {
        // Validation of unknown values lives at config-load time (V-19),
        // not in the resolver. If an unknown value reaches the resolver
        // anyway, it's surfaced verbatim with Explicit provenance.
        var resolved = RequirementInputResolver.Resolve(
            Type(executionMode: "serial"), childCount: 0);

        resolved.ExecutionMode.ShouldBe("serial");
        resolved.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_ExecutionModeDefault_DoesNotMarkAnyInferred()
    {
        // Default (static fallback) is distinct from Inferred (heuristic) —
        // unset execution_mode must NOT flip AnyInferred on its own.
        var resolved = RequirementInputResolver.Resolve(
            Type(decomposable: true), childCount: 0);

        resolved.ExecutionModeProvenance.ShouldBe(ResolutionProvenance.Default);
        resolved.AnyInferred.ShouldBeFalse();
    }

    // ── facets override (closed-loop PR #7) ─────────────────────────────

    [Fact]
    public void Resolve_OverrideFacetsNull_FallsBackToTypeConfigFacets()
    {
        var resolved = RequirementInputResolver.Resolve(
            Type(facets: ["plannable"]),
            childCount: 0,
            overrideFacets: null);

        resolved.Facets.ShouldBe(["plannable"]);
        resolved.FacetsProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_OverrideFacetsEmpty_FallsBackToTypeConfigFacets()
    {
        // Empty override is semantically equivalent to "no override declared"
        // — must NOT zero out the type-config default.
        var resolved = RequirementInputResolver.Resolve(
            Type(facets: ["implementable"]),
            childCount: 0,
            overrideFacets: []);

        resolved.Facets.ShouldBe(["implementable"]);
        resolved.FacetsProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_OverrideFacetsNonEmpty_ReplacesTypeConfigFacets()
    {
        // Override wins — the architect-declared apex_facets is a stronger
        // signal than the type-config default.
        var resolved = RequirementInputResolver.Resolve(
            Type(facets: ["plannable"]),
            childCount: 0,
            overrideFacets: ["implementable"]);

        resolved.Facets.ShouldBe(["implementable"]);
        resolved.FacetsProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    [Fact]
    public void Resolve_NoOverride_NoConfigFacets_ReturnsEmptyDefaultProvenance()
    {
        var resolved = RequirementInputResolver.Resolve(Type(), childCount: 0);

        resolved.Facets.ShouldBeEmpty();
        resolved.FacetsProvenance.ShouldBe(ResolutionProvenance.Default);
    }

    [Fact]
    public void Resolve_OverrideDoesNotMutateTypeConfig()
    {
        // The override must be per-call; the type-config object is shared
        // and must not be mutated.
        var type = Type(facets: ["plannable"]);
        var originalFacets = type.Facets;

        RequirementInputResolver.Resolve(type, childCount: 0, overrideFacets: ["implementable"]);

        type.Facets.ShouldBeSameAs(originalFacets);
        type.Facets.ShouldBe(["plannable"]);
    }
}
