using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for <see cref="RequirementSetReducer.Apply"/>. The reducer is the
/// single point where dispositions get computed for the workflow layer, so
/// these tests aim to lock down its observable behaviour exhaustively:
/// overlay semantics, threshold semantics, fixpoint propagation, defensive
/// handling of invalid observer input, and edge-case shapes (empty sets,
/// no-edge sets, missing prerequisites).
/// </summary>
public sealed class RequirementSetReducerTests
{
    private static Requirement Req(string kind, string disp = Disposition.Needed) =>
        new(kind, disp, AcceptanceCriteria: null);

    private static RequirementEdge Edge(string from, string to, string threshold = Disposition.Satisfied) =>
        new(from, to, threshold, RequirementEdgeSource.Definitional);

    private static ObservedRequirementState Observe(params (string Kind, string Disp)[] pairs) =>
        ObservedRequirementState.From(pairs.Select(p => new KeyValuePair<string, string>(p.Kind, p.Disp)));

    private static string DispositionOf(RequirementSet set, string kind) =>
        set.Items.Single(r => r.Kind == kind).Disposition;

    // ── overlay ──────────────────────────────────────────────────────────

    [Fact]
    public void Apply_EmptySet_ReturnsEmptyItems()
    {
        var result = RequirementSetReducer.Apply(
            new RequirementSet([], []),
            ObservedRequirementState.Empty);

        result.Items.ShouldBeEmpty();
        result.Edges.ShouldBeEmpty();
    }

    [Fact]
    public void Apply_NoObservation_PromotesAllNoPrereqRequirementsToReady()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanAuthored), Req(RequirementKind.ImplementationMerged)],
            []);

        var result = RequirementSetReducer.Apply(set, ObservedRequirementState.Empty);

        DispositionOf(result, RequirementKind.PlanAuthored).ShouldBe(Disposition.Ready);
        DispositionOf(result, RequirementKind.ImplementationMerged).ShouldBe(Disposition.Ready);
    }

    [Fact]
    public void Apply_ObservedSatisfied_PreservedThroughOverlay()
    {
        var set = new RequirementSet([Req(RequirementKind.PlanAuthored)], []);
        var observed = Observe((RequirementKind.PlanAuthored, Disposition.Satisfied));

        var result = RequirementSetReducer.Apply(set, observed);

        DispositionOf(result, RequirementKind.PlanAuthored).ShouldBe(Disposition.Satisfied);
    }

    [Fact]
    public void Apply_ObservedFulfilling_PreservedThroughOverlay()
    {
        var set = new RequirementSet([Req(RequirementKind.ImplementationMerged)], []);
        var observed = Observe((RequirementKind.ImplementationMerged, Disposition.Fulfilling));

        var result = RequirementSetReducer.Apply(set, observed);

        DispositionOf(result, RequirementKind.ImplementationMerged).ShouldBe(Disposition.Fulfilling);
    }

    [Fact]
    public void Apply_ObservedReady_DefensivelyCoercedToNeededThenPromoted()
    {
        // Observers are not allowed to emit Ready. The reducer coerces it to
        // Needed and then re-derives readiness from prerequisites — for an
        // unblocked requirement this round-trips back to Ready.
        var set = new RequirementSet([Req(RequirementKind.PlanAuthored)], []);
        var observed = Observe((RequirementKind.PlanAuthored, Disposition.Ready));

        var result = RequirementSetReducer.Apply(set, observed);

        DispositionOf(result, RequirementKind.PlanAuthored).ShouldBe(Disposition.Ready);
    }

    // ── prereq gating ────────────────────────────────────────────────────

    [Fact]
    public void Apply_NeededWithUnsatisfiedPrereq_StaysNeeded()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanAuthored), Req(RequirementKind.PlanReviewed)],
            [Edge(RequirementKind.PlanAuthored, RequirementKind.PlanReviewed)]);

        var result = RequirementSetReducer.Apply(set, ObservedRequirementState.Empty);

        // PlanAuthored has no incoming edges → Ready.
        DispositionOf(result, RequirementKind.PlanAuthored).ShouldBe(Disposition.Ready);
        // PlanReviewed depends on PlanAuthored=Satisfied; observation is empty
        // so PlanAuthored is only Ready, not Satisfied → blocked.
        DispositionOf(result, RequirementKind.PlanReviewed).ShouldBe(Disposition.Needed);
    }

    [Fact]
    public void Apply_NeededWithSatisfiedPrereq_PromotedToReady()
    {
        var set = new RequirementSet(
            [Req(RequirementKind.PlanAuthored), Req(RequirementKind.PlanReviewed)],
            [Edge(RequirementKind.PlanAuthored, RequirementKind.PlanReviewed)]);

        var observed = Observe((RequirementKind.PlanAuthored, Disposition.Satisfied));

        var result = RequirementSetReducer.Apply(set, observed);

        DispositionOf(result, RequirementKind.PlanReviewed).ShouldBe(Disposition.Ready);
    }

    [Fact]
    public void Apply_FixpointPropagatesThroughChain()
    {
        // PlanAuthored → PlanReviewed → PlanPromoted, all needed; no observation.
        // PlanAuthored has no incoming edges so it promotes to Ready, but
        // Ready does NOT meet the Satisfied threshold, so the chain stops.
        var set = new RequirementSet(
            [
                Req(RequirementKind.PlanAuthored),
                Req(RequirementKind.PlanReviewed),
                Req(RequirementKind.PlanPromoted),
            ],
            [
                Edge(RequirementKind.PlanAuthored, RequirementKind.PlanReviewed),
                Edge(RequirementKind.PlanReviewed, RequirementKind.PlanPromoted),
            ]);

        var result = RequirementSetReducer.Apply(set, ObservedRequirementState.Empty);

        DispositionOf(result, RequirementKind.PlanAuthored).ShouldBe(Disposition.Ready);
        DispositionOf(result, RequirementKind.PlanReviewed).ShouldBe(Disposition.Needed);
        DispositionOf(result, RequirementKind.PlanPromoted).ShouldBe(Disposition.Needed);
    }

    [Fact]
    public void Apply_FixpointPropagatesWhenChainSatisfied()
    {
        var set = new RequirementSet(
            [
                Req(RequirementKind.PlanAuthored),
                Req(RequirementKind.PlanReviewed),
                Req(RequirementKind.PlanPromoted),
            ],
            [
                Edge(RequirementKind.PlanAuthored, RequirementKind.PlanReviewed),
                Edge(RequirementKind.PlanReviewed, RequirementKind.PlanPromoted),
            ]);

        var observed = Observe(
            (RequirementKind.PlanAuthored, Disposition.Satisfied),
            (RequirementKind.PlanReviewed, Disposition.Satisfied));

        var result = RequirementSetReducer.Apply(set, observed);

        DispositionOf(result, RequirementKind.PlanAuthored).ShouldBe(Disposition.Satisfied);
        DispositionOf(result, RequirementKind.PlanReviewed).ShouldBe(Disposition.Satisfied);
        DispositionOf(result, RequirementKind.PlanPromoted).ShouldBe(Disposition.Ready);
    }

    [Fact]
    public void Apply_AllPrereqsRequired_NotJustOne()
    {
        // Two independent prereqs both pointing at one dependent.
        var set = new RequirementSet(
            [
                Req(RequirementKind.PlanAuthored),
                Req(RequirementKind.ChildrenSeeded),
                Req(RequirementKind.ImplementationMerged),
            ],
            [
                Edge(RequirementKind.PlanAuthored, RequirementKind.ImplementationMerged),
                Edge(RequirementKind.ChildrenSeeded, RequirementKind.ImplementationMerged),
            ]);

        // Only one of the two prereqs satisfied — dependent must remain Needed.
        var observed = Observe((RequirementKind.PlanAuthored, Disposition.Satisfied));
        var partial = RequirementSetReducer.Apply(set, observed);
        DispositionOf(partial, RequirementKind.ImplementationMerged).ShouldBe(Disposition.Needed);

        // Both satisfied — dependent promotes.
        observed = Observe(
            (RequirementKind.PlanAuthored, Disposition.Satisfied),
            (RequirementKind.ChildrenSeeded, Disposition.Satisfied));
        var full = RequirementSetReducer.Apply(set, observed);
        DispositionOf(full, RequirementKind.ImplementationMerged).ShouldBe(Disposition.Ready);
    }

    [Fact]
    public void Apply_LowerThresholdEdge_PromotesEarlier()
    {
        // Edge requires only Fulfilling rather than Satisfied.
        var set = new RequirementSet(
            [Req(RequirementKind.PlanAuthored), Req(RequirementKind.PlanReviewed)],
            [Edge(RequirementKind.PlanAuthored, RequirementKind.PlanReviewed, Disposition.Fulfilling)]);

        var observed = Observe((RequirementKind.PlanAuthored, Disposition.Fulfilling));

        var result = RequirementSetReducer.Apply(set, observed);

        DispositionOf(result, RequirementKind.PlanAuthored).ShouldBe(Disposition.Fulfilling);
        DispositionOf(result, RequirementKind.PlanReviewed).ShouldBe(Disposition.Ready);
    }

    [Fact]
    public void Apply_EdgeReferencesUnknownPrereq_TreatedAsUnmet()
    {
        // Hand-built set with a dangling edge; the reducer must not crash and
        // must conservatively keep the dependent Needed.
        var set = new RequirementSet(
            [Req(RequirementKind.PlanReviewed)],
            [Edge("nonexistent_kind", RequirementKind.PlanReviewed)]);

        var result = RequirementSetReducer.Apply(set, ObservedRequirementState.Empty);

        DispositionOf(result, RequirementKind.PlanReviewed).ShouldBe(Disposition.Needed);
    }

    [Fact]
    public void Apply_PreservesOriginalEdges()
    {
        // The reducer rewrites items but must leave the edge list untouched —
        // downstream consumers (e.g. diagnostics) rely on edge identity.
        var edges = new List<RequirementEdge>
        {
            Edge(RequirementKind.PlanAuthored, RequirementKind.PlanReviewed),
        };
        var set = new RequirementSet(
            [Req(RequirementKind.PlanAuthored), Req(RequirementKind.PlanReviewed)],
            edges);

        var result = RequirementSetReducer.Apply(set, ObservedRequirementState.Empty);

        result.Edges.ShouldBeSameAs(edges);
    }

    [Fact]
    public void Apply_NullArguments_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            RequirementSetReducer.Apply(null!, ObservedRequirementState.Empty));

        Should.Throw<ArgumentNullException>(() =>
            RequirementSetReducer.Apply(new RequirementSet([], []), null!));
    }
}
