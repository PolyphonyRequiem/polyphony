using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for <see cref="EdgeGraph"/> and its helper
/// <see cref="CrossItemEdgeDeriver"/>. PR #1 of the Phase 7 edges arc:
/// definitional bucket only (children-unblock + terminal rollup); no
/// conflict detection (PR #2); no policy or planner-declared buckets.
/// </summary>
public class EdgeGraphTests
{
    // -------- helpers ----------------------------------------------------

    private static EdgeGraphInput Item(int id, int parentId, params string[] facets)
    {
        var derivation = RequirementSetDeriver.Derive(facets, decomposable: false);
        derivation.IsValid.ShouldBeTrue($"item {id} derivation must be valid");
        return new EdgeGraphInput(id, parentId, derivation.Set!);
    }

    private static EdgeGraphInput Container(int id, int parentId, params string[] facets)
    {
        // Decomposable parent — emits children_seeded when plannable+decomposable,
        // emits only item_satisfied when an empty pure container.
        var derivation = RequirementSetDeriver.Derive(facets, decomposable: true);
        derivation.IsValid.ShouldBeTrue($"item {id} derivation must be valid");
        return new EdgeGraphInput(id, parentId, derivation.Set!);
    }

    private static IReadOnlyDictionary<int, EdgeGraphInput> Map(params EdgeGraphInput[] items)
        => items.ToDictionary(i => i.ItemId);

    // -------- contract checks --------------------------------------------

    [Fact]
    public void Build_NullInputs_Throws()
    {
        Should.Throw<ArgumentNullException>(() => EdgeGraph.Build(null!));
    }

    [Fact]
    public void Build_EmptyInputs_Throws()
    {
        Should.Throw<ArgumentException>(() => EdgeGraph.Build([]));
    }

    [Fact]
    public void Build_DuplicateItemIds_Throws()
    {
        var ex = Should.Throw<ArgumentException>(() => EdgeGraph.Build([
            Item(100, parentId: 0, "implementable"),
            Item(100, parentId: 0, "implementable"),
        ]));
        ex.Message.ShouldContain("100");
    }

    // -------- single-item graphs ----------------------------------------

    [Fact]
    public void Build_SingleLeaf_NoCrossItemEdges_OneWave()
    {
        var graph = EdgeGraph.Build([Item(100, parentId: 0, "implementable")]);

        graph.Edges.ShouldBeEmpty();
        graph.Conflicts.ShouldBeEmpty();
        graph.ItemRequirements.ShouldContainKey(100);

        var waves = graph.ToWaves();
        waves.Count.ShouldBe(1);
        waves[0].WaveIndex.ShouldBe(0);
        waves[0].ItemIds.ShouldBe([100]);
    }

    [Fact]
    public void Build_PureContainerAlone_OneWave()
    {
        // Pure container with no facets — only ItemSatisfied. No children
        // means no cross-item edges. Dispatchable in wave 0.
        var graph = EdgeGraph.Build([Container(100, parentId: 0)]);

        graph.Edges.ShouldBeEmpty();
        var waves = graph.ToWaves();
        waves.Count.ShouldBe(1);
        waves[0].ItemIds.ShouldBe([100]);
    }

    // -------- children-unblock rule --------------------------------------

    [Fact]
    public void Build_PlannableParentWithChild_EmitsChildrenUnblockEdge()
    {
        // Parent: plannable + decomposable → carries children_seeded.
        // Child: implementable leaf → entry requirement is implementation_merged.
        // Expected unblock edge: parent.children_seeded → child.implementation_merged.
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0, "plannable"),
            Item(200, parentId: 100, "implementable"),
        ]);

        var unblocks = graph.Edges
            .Where(e => e.PrerequisiteKind == RequirementKind.ChildrenSeeded)
            .ToList();
        unblocks.Count.ShouldBe(1);
        unblocks[0].PrerequisiteItemId.ShouldBe(100);
        unblocks[0].DependentItemId.ShouldBe(200);
        unblocks[0].DependentKind.ShouldBe(RequirementKind.ImplementationMerged);
        unblocks[0].RequiredDisposition.ShouldBe(Disposition.Satisfied);
        unblocks[0].Source.ShouldBe(RequirementEdgeSource.Definitional);
    }

    [Fact]
    public void Build_PureContainerParent_NoChildrenUnblockEdge()
    {
        // Pure container parent has no children_seeded — child is unblocked
        // from the start (terminal rollup is the only cross-item edge).
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0),  // empty pure container
            Item(200, parentId: 100, "implementable"),
        ]);

        graph.Edges.ShouldNotContain(e => e.PrerequisiteKind == RequirementKind.ChildrenSeeded);
    }

    // -------- terminal rollup rule ---------------------------------------

    [Fact]
    public void Build_ParentChild_EmitsTerminalRollupEdge()
    {
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0, "plannable"),
            Item(200, parentId: 100, "implementable"),
        ]);

        var rollups = graph.Edges
            .Where(e => e.PrerequisiteKind == RequirementKind.ItemSatisfied)
            .ToList();
        rollups.Count.ShouldBe(1);
        rollups[0].PrerequisiteItemId.ShouldBe(200);
        rollups[0].DependentItemId.ShouldBe(100);
        rollups[0].DependentKind.ShouldBe(RequirementKind.ItemSatisfied);
        rollups[0].RequiredDisposition.ShouldBe(Disposition.Satisfied);
        rollups[0].Source.ShouldBe(RequirementEdgeSource.Definitional);
    }

    [Fact]
    public void ToWaves_TerminalRollupDoesNotGateDispatch()
    {
        // Pure container parent + leaf child. The rollup edge
        // (child.ItemSatisfied → parent.ItemSatisfied) targets a non-entry
        // requirement, so it must NOT gate dispatch — both items are in
        // wave 0 (parent has no children_seeded, child has no unblock edge).
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0),
            Item(200, parentId: 100, "implementable"),
        ]);

        var waves = graph.ToWaves();
        waves.Count.ShouldBe(1);
        waves[0].ItemIds.ShouldBe([100, 200]);
    }

    // -------- multi-wave dispatch ----------------------------------------

    [Fact]
    public void ToWaves_PlannableParentChild_TwoWaves()
    {
        // Parent (plannable + decomposable) gates child via children_seeded.
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0, "plannable"),
            Item(200, parentId: 100, "implementable"),
        ]);

        var waves = graph.ToWaves();
        waves.Count.ShouldBe(2);
        waves[0].ItemIds.ShouldBe([100]);
        waves[1].ItemIds.ShouldBe([200]);
    }

    [Fact]
    public void ToWaves_EpicIssueTask_ThreeWaves()
    {
        // Epic → Issue → Task, each parent plannable + decomposable.
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0, "plannable"),
            Container(200, parentId: 100, "plannable"),
            Item(300, parentId: 200, "implementable"),
        ]);

        var waves = graph.ToWaves();
        waves.Count.ShouldBe(3);
        waves[0].ItemIds.ShouldBe([100]);
        waves[1].ItemIds.ShouldBe([200]);
        waves[2].ItemIds.ShouldBe([300]);
    }

    [Fact]
    public void ToWaves_DeterministicOrderById()
    {
        // Two siblings under one plannable parent — both unblocked by the
        // parent in wave 1, sorted by id ascending.
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0, "plannable"),
            Item(300, parentId: 100, "implementable"),
            Item(200, parentId: 100, "implementable"),
        ]);

        var waves = graph.ToWaves();
        waves.Count.ShouldBe(2);
        waves[0].ItemIds.ShouldBe([100]);
        waves[1].ItemIds.ShouldBe([200, 300]);  // sorted ascending
    }

    // -------- PR #1 invariants -------------------------------------------

    [Fact]
    public void Build_PR1_ConflictsAlwaysEmpty()
    {
        // PR #1 ships no conflict detection — the list is always empty
        // even on inputs that PR #2 will flag as conflicting.
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0, "plannable"),
            Item(200, parentId: 100, "implementable"),
        ]);
        graph.Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public void Build_AllCrossItemEdges_DefinitionalAndSatisfied()
    {
        var graph = EdgeGraph.Build([
            Container(100, parentId: 0, "plannable"),
            Container(200, parentId: 100, "plannable"),
            Item(300, parentId: 200, "implementable"),
        ]);

        foreach (var edge in graph.Edges)
        {
            edge.Source.ShouldBe(RequirementEdgeSource.Definitional);
            edge.RequiredDisposition.ShouldBe(Disposition.Satisfied);
        }
    }
}
