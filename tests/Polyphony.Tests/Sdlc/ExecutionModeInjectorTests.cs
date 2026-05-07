using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for <see cref="ExecutionModeInjector.Inject"/>. The injector is the
/// layer that translates the resolved <c>execution_mode</c> knob into a
/// concrete edge addition; these tests pin down the per-mode behaviour
/// (no-op vs. append), the idempotence guarantee, the source/threshold
/// attribution of the injected edge, and the composition with the reducer
/// (mode-driven gating actually shows up in dispatch).
/// </summary>
public sealed class ExecutionModeInjectorTests
{
    // ── helpers ──────────────────────────────────────────────────────────

    private static Requirement Req(string kind) =>
        new(kind, Disposition.Needed, AcceptanceCriteria: null);

    private static RequirementEdge Edge(
        string from, string to, string threshold = Disposition.Satisfied,
        string source = RequirementEdgeSource.Definitional) =>
        new(from, to, threshold, source);

    private static string DispositionOf(RequirementSet set, string kind) =>
        set.Items.Single(r => r.Kind == kind).Disposition;

    private static ObservedRequirementState Observe(
        params (string Kind, string Disp)[] pairs) =>
        ObservedRequirementState.From(
            pairs.Select(p => new KeyValuePair<string, string>(p.Kind, p.Disp)));

    // ── Parallel mode (and unknown) → no-op same-instance ───────────────

    [Fact]
    public void Inject_Parallel_ReturnsSameInstance()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            []);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.Parallel);

        result.ShouldBeSameAs(set);
    }

    [Fact]
    public void Inject_UnknownMode_TreatsAsParallel()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            []);

        // Deliberately invalid: validation lives at config-load time. The
        // injector must fail open at runtime — never throw on stale input.
        var result = ExecutionModeInjector.Inject(set, "serial");

        result.ShouldBeSameAs(set);
    }

    [Fact]
    public void Inject_NullMode_TreatsAsParallel()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            []);

        var result = ExecutionModeInjector.Inject(set, null!);

        result.ShouldBeSameAs(set);
    }

    // ── PlanThenImplement: append when both kinds present ───────────────

    [Fact]
    public void Inject_PlanThenImplement_PlanAndImplPresent_AppendsEdge()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            []);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);

        result.ShouldNotBeSameAs(set);
        result.Edges.Count.ShouldBe(1);
        result.Edges[0].PrerequisiteKind.ShouldBe(RequirementKind.PlanPromoted);
        result.Edges[0].DependentKind.ShouldBe(RequirementKind.ImplementationMerged);
    }

    // ── PlanThenImplement: missing-half no-ops ──────────────────────────

    [Fact]
    public void Inject_PlanThenImplement_PlanMissing_NoChange()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.ImplementationMerged), Req(RequirementKind.ItemSatisfied)],
            [Edge(RequirementKind.ImplementationMerged, RequirementKind.ItemSatisfied)]);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);

        result.ShouldBeSameAs(set);
    }

    [Fact]
    public void Inject_PlanThenImplement_ImplMissing_NoChange()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanAuthored),
             Req(RequirementKind.PlanReviewed),
             Req(RequirementKind.PlanPromoted)],
            [Edge(RequirementKind.PlanAuthored, RequirementKind.PlanReviewed),
             Edge(RequirementKind.PlanReviewed, RequirementKind.PlanPromoted)]);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);

        result.ShouldBeSameAs(set);
    }

    // ── Idempotence ─────────────────────────────────────────────────────

    [Fact]
    public void Inject_PlanThenImplement_EdgeAlreadyPresent_Idempotent()
    {
        var preexisting = Edge(
            RequirementKind.PlanPromoted, RequirementKind.ImplementationMerged);
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            [preexisting]);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);

        result.ShouldBeSameAs(set);
        result.Edges.Count.ShouldBe(1);
    }

    [Fact]
    public void Inject_PlanThenImplement_DoubleApplication_IsStable()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            []);

        var once = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);
        var twice = ExecutionModeInjector.Inject(once, ExecutionMode.PlanThenImplement);

        // Second pass must not duplicate the appended edge.
        twice.ShouldBeSameAs(once);
        twice.Edges.Count.ShouldBe(1);
    }

    // ── Existing edges preserved ────────────────────────────────────────

    [Fact]
    public void Inject_PlanThenImplement_PreservesExistingEdges()
    {
        var planAuthored = Edge(
            RequirementKind.PlanAuthored, RequirementKind.PlanReviewed);
        var planReviewed = Edge(
            RequirementKind.PlanReviewed, RequirementKind.PlanPromoted);
        var implToTerminal = Edge(
            RequirementKind.ImplementationMerged, RequirementKind.ItemSatisfied);
        var set = new RequirementSet(
            [Req(RequirementKind.PlanAuthored),
             Req(RequirementKind.PlanReviewed),
             Req(RequirementKind.PlanPromoted),
             Req(RequirementKind.ImplementationMerged),
             Req(RequirementKind.ItemSatisfied)],
            [planAuthored, planReviewed, implToTerminal]);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);

        result.Edges.Count.ShouldBe(4);
        result.Edges.ShouldContain(planAuthored);
        result.Edges.ShouldContain(planReviewed);
        result.Edges.ShouldContain(implToTerminal);
        result.Edges.ShouldContain(e =>
            e.PrerequisiteKind == RequirementKind.PlanPromoted &&
            e.DependentKind == RequirementKind.ImplementationMerged);
        // Items must not be re-allocated.
        result.Items.ShouldBeSameAs(set.Items);
    }

    // ── Source / threshold attribution ──────────────────────────────────

    [Fact]
    public void Inject_PlanThenImplement_InjectedEdgeIsDefinitional()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            []);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);

        var injected = result.Edges.Single(e =>
            e.PrerequisiteKind == RequirementKind.PlanPromoted &&
            e.DependentKind == RequirementKind.ImplementationMerged);
        injected.Source.ShouldBe(RequirementEdgeSource.Definitional);
    }

    [Fact]
    public void Inject_PlanThenImplement_InjectedEdgeRequiresSatisfied()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            []);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);

        var injected = result.Edges.Single(e =>
            e.PrerequisiteKind == RequirementKind.PlanPromoted &&
            e.DependentKind == RequirementKind.ImplementationMerged);
        injected.RequiredDisposition.ShouldBe(Disposition.Satisfied);
    }

    // ── Pre-existing edge with non-default attribution is preserved ─────

    [Fact]
    public void Inject_PlanThenImplement_PreexistingEdgeFromAnotherSource_IsNotReplaced()
    {
        // Hypothetical future case: a planner-declared edge already exists.
        // The injector treats "edge with same endpoints" as already-present
        // regardless of source/threshold; idempotence wins over normalisation.
        var preexisting = new RequirementEdge(
            RequirementKind.PlanPromoted,
            RequirementKind.ImplementationMerged,
            Disposition.Satisfied,
            RequirementEdgeSource.PlannerDeclared);
        var set = new RequirementSet(
            [Req(RequirementKind.PlanPromoted), Req(RequirementKind.ImplementationMerged)],
            [preexisting]);

        var result = ExecutionModeInjector.Inject(set, ExecutionMode.PlanThenImplement);

        result.ShouldBeSameAs(set);
        result.Edges.Single().Source.ShouldBe(RequirementEdgeSource.PlannerDeclared);
    }

    // ── Round-trip composition with the reducer ─────────────────────────

    /// <summary>
    /// End-to-end demonstration that injecting <see cref="ExecutionMode.PlanThenImplement"/>
    /// changes the dispatch model: <c>implementation_merged</c> stays
    /// <see cref="Disposition.Needed"/> until <c>plan_promoted</c> is observed
    /// as <see cref="Disposition.Satisfied"/>. Constructed against a hand-built
    /// set with no pre-existing gate to make the mode's effect unambiguous —
    /// the deriver also wires this edge transitively in real use, but the
    /// injector's contract is independent of that.
    /// </summary>
    [Fact]
    public void Inject_PlanThenImplement_GatesImplementationInReducer()
    {
        // Hand-built set: plan_promoted and implementation_merged with no
        // gating edge between them. Without injection, both would promote
        // to Ready immediately under empty observation.
        var bareSet = new RequirementSet(
            [Req(RequirementKind.PlanPromoted),
             Req(RequirementKind.ImplementationMerged),
             Req(RequirementKind.ItemSatisfied)],
            [Edge(RequirementKind.PlanPromoted, RequirementKind.ItemSatisfied),
             Edge(RequirementKind.ImplementationMerged, RequirementKind.ItemSatisfied)]);

        var bareReduced = RequirementSetReducer.Apply(bareSet, ObservedRequirementState.Empty);
        DispositionOf(bareReduced, RequirementKind.PlanPromoted).ShouldBe(Disposition.Ready);
        DispositionOf(bareReduced, RequirementKind.ImplementationMerged).ShouldBe(Disposition.Ready);

        // Inject the mode: implementation_merged now has a Satisfied-threshold
        // dependency on plan_promoted.
        var gated = ExecutionModeInjector.Inject(bareSet, ExecutionMode.PlanThenImplement);

        // Empty observation: plan_promoted is Ready (no prereqs), but
        // implementation_merged stays Needed because its prereq is only Ready.
        var empty = RequirementSetReducer.Apply(gated, ObservedRequirementState.Empty);
        DispositionOf(empty, RequirementKind.PlanPromoted).ShouldBe(Disposition.Ready);
        DispositionOf(empty, RequirementKind.ImplementationMerged).ShouldBe(Disposition.Needed);

        // plan_promoted Fulfilling is still not enough — threshold is Satisfied.
        var fulfilling = RequirementSetReducer.Apply(
            gated,
            Observe((RequirementKind.PlanPromoted, Disposition.Fulfilling)));
        DispositionOf(fulfilling, RequirementKind.ImplementationMerged).ShouldBe(Disposition.Needed);

        // plan_promoted Satisfied: implementation_merged finally promotes.
        var satisfied = RequirementSetReducer.Apply(
            gated,
            Observe((RequirementKind.PlanPromoted, Disposition.Satisfied)));
        DispositionOf(satisfied, RequirementKind.PlanPromoted).ShouldBe(Disposition.Satisfied);
        DispositionOf(satisfied, RequirementKind.ImplementationMerged).ShouldBe(Disposition.Ready);
    }

    /// <summary>
    /// Composition with the deriver: <c>{plannable, implementable}</c>
    /// non-decomposable already wires the gate definitionally, so injection
    /// is idempotent on real deriver output. This locks down the no-regression
    /// guarantee: layering the injector on top of the deriver never adds a
    /// duplicate edge or mutates the existing one.
    /// </summary>
    [Fact]
    public void Inject_PlanThenImplement_OnDeriverOutput_IsIdempotent()
    {
        var derivation = RequirementSetDeriver.Derive(
            ["plannable", "implementable"], decomposable: false);
        derivation.IsValid.ShouldBeTrue();
        var derived = derivation.Set!;

        // Sanity: the deriver already produced the gate.
        derived.Edges.ShouldContain(e =>
            e.PrerequisiteKind == RequirementKind.PlanPromoted &&
            e.DependentKind == RequirementKind.ImplementationMerged);
        var beforeCount = derived.Edges.Count;

        var injected = ExecutionModeInjector.Inject(derived, ExecutionMode.PlanThenImplement);

        injected.ShouldBeSameAs(derived);
        injected.Edges.Count.ShouldBe(beforeCount);
    }
}
