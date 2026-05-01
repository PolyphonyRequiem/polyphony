using NSubstitute;
using Polyphony.Configuration;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Verifies that HierarchyWalker correctly reports plannable depth in its output
/// so that the depth guard can enforce the budget.
///
/// <b>Architectural boundary:</b> Depth budget enforcement is handled by
/// <c>scripts/depth-guard.ps1</c> (a deterministic conductor agent), which receives
/// the current recursion depth and emits <c>allowed=false</c> when the limit is
/// exceeded. The .NET layer's responsibility is to <em>accurately represent</em> the
/// hierarchy with capability annotations — it does not reject deep trees itself.
/// Pester tests in <c>scripts/depth-guard.Tests.ps1</c> cover the enforcement logic.
/// These xUnit tests verify the .NET side of the contract.
/// </summary>
public sealed class DepthBudgetTests
{
    private readonly IWorkItemRepository _repository = Substitute.For<IWorkItemRepository>();

    /// <summary>
    /// Process config with 5 plannable types to enable deep plannable hierarchies.
    /// "Portfolio", "Initiative", "Epic", "Feature", and "Story" are all plannable;
    /// "Task" is implementable-only.
    /// </summary>
    private readonly ProcessConfig _config = new ProcessConfigBuilder()
        .WithType("Portfolio", ["plannable"])
        .WithType("Initiative", ["plannable"])
        .WithType("Epic", ["plannable"])
        .WithType("Feature", ["plannable"])
        .WithType("Story", ["plannable", "implementable"])
        .WithType("Task", ["implementable"])
        .Build();

    private HierarchyWalker CreateWalker() => new(_config, _repository);

    /// <summary>
    /// Recursively counts the maximum number of nested plannable levels
    /// in a <see cref="HierarchyResult"/> tree. Only nodes whose
    /// <see cref="HierarchyResult.Capabilities"/> contain "plannable" are counted.
    /// </summary>
    private static int CountMaxPlannableDepth(HierarchyResult? node)
    {
        if (node is null)
            return 0;

        var isPlannable = node.Capabilities.Contains("plannable");
        var childMax = 0;

        if (node.Children is { Length: > 0 })
        {
            foreach (var child in node.Children)
            {
                var childDepth = CountMaxPlannableDepth(child);
                if (childDepth > childMax)
                    childMax = childDepth;
            }
        }

        return isPlannable ? 1 + childMax : childMax;
    }

    // ──────────────────────────────────────────────
    //  Plannable depth counting from HierarchyResult
    // ──────────────────────────────────────────────

    [Fact]
    public async Task SinglePlannableNode_HasPlannableDepthOfOne()
    {
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Root Epic").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);

        var result = await CreateWalker().WalkAsync(1, maxDepth: 0, CancellationToken.None);

        result.ShouldNotBeNull();
        CountMaxPlannableDepth(result).ShouldBe(1);
    }

    [Fact]
    public async Task ImplementableOnlyNode_HasPlannableDepthOfZero()
    {
        var task = new WorkItemBuilder()
            .WithId(1).WithType("Task").WithTitle("Leaf Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(task);

        var result = await CreateWalker().WalkAsync(1, maxDepth: 0, CancellationToken.None);

        result.ShouldNotBeNull();
        CountMaxPlannableDepth(result).ShouldBe(0);
    }

    [Fact]
    public async Task TwoPlannableLevels_ReportsDepthTwo()
    {
        // Epic → Feature (both plannable)
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(10).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var result = await CreateWalker().WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        CountMaxPlannableDepth(result).ShouldBe(2);
    }

    [Fact]
    public async Task FourPlannableLevels_WithinBudget()
    {
        // Portfolio → Initiative → Epic → Feature (4 plannable levels = within budget)
        var portfolio = new WorkItemBuilder()
            .WithId(1).WithType("Portfolio").WithTitle("Portfolio").WithState("Doing").Build();
        var initiative = new WorkItemBuilder()
            .WithId(10).WithType("Initiative").WithTitle("Initiative").WithState("Doing").Build();
        var epic = new WorkItemBuilder()
            .WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(1000).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(portfolio);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { initiative });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { epic });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature });
        _repository.GetChildrenAsync(1000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var result = await CreateWalker().WalkAsync(1, maxDepth: 5, CancellationToken.None);

        result.ShouldNotBeNull();
        var plannableDepth = CountMaxPlannableDepth(result);
        plannableDepth.ShouldBe(4);
        // 4 plannable levels is within the budget (≤4 is acceptable)
        plannableDepth.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task FivePlannableLevels_ExceedsBudget()
    {
        // Portfolio → Initiative → Epic → Feature → Story (5 plannable levels = exceeds budget)
        // depth-guard.ps1 would set allowed=false when depth ≥ MaxDepth.
        // The .NET layer must faithfully report all 5 levels so the PS guard can detect it.
        var portfolio = new WorkItemBuilder()
            .WithId(1).WithType("Portfolio").WithTitle("Portfolio").WithState("Doing").Build();
        var initiative = new WorkItemBuilder()
            .WithId(10).WithType("Initiative").WithTitle("Initiative").WithState("Doing").Build();
        var epic = new WorkItemBuilder()
            .WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(1000).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();
        var story = new WorkItemBuilder()
            .WithId(10000).WithType("Story").WithTitle("Story").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(portfolio);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { initiative });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { epic });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature });
        _repository.GetChildrenAsync(1000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { story });
        _repository.GetChildrenAsync(10000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var result = await CreateWalker().WalkAsync(1, maxDepth: 6, CancellationToken.None);

        result.ShouldNotBeNull();
        var plannableDepth = CountMaxPlannableDepth(result);
        plannableDepth.ShouldBe(5);
        // >4 plannable levels exceeds the budget — depth-guard.ps1 would reject this
        plannableDepth.ShouldBeGreaterThan(4);
    }

    // ──────────────────────────────────────────────
    //  Non-plannable nodes don't inflate depth count
    // ──────────────────────────────────────────────

    [Fact]
    public async Task PlannableWithImplementableLeaf_DoesNotCountImplementableInDepth()
    {
        // Epic (plannable) → Task (implementable) — plannable depth is 1, not 2
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var result = await CreateWalker().WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        CountMaxPlannableDepth(result).ShouldBe(1);
    }

    [Fact]
    public async Task InterleavedPlannableAndImplementable_CountsOnlyPlannableNodes()
    {
        // Epic (plannable) → Task (implementable) — only 1 plannable level despite 2 total levels
        // This verifies that implementable-only nodes interleaved in the tree
        // don't inflate the plannable depth count.
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithTitle("Task").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });

        var result = await CreateWalker().WalkAsync(1, maxDepth: 2, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Capabilities.ShouldContain("plannable");
        result.Children[0].Capabilities.ShouldNotContain("plannable");
        CountMaxPlannableDepth(result).ShouldBe(1);
    }

    // ──────────────────────────────────────────────
    //  maxDepth parameter truncates before exceeding budget
    // ──────────────────────────────────────────────

    [Fact]
    public async Task MaxDepthTruncation_PreventsDeepPlannableTraversal()
    {
        // Tree has 5 plannable levels, but maxDepth=3 truncates at level 3.
        // The walker should only return 3 plannable levels.
        var portfolio = new WorkItemBuilder()
            .WithId(1).WithType("Portfolio").WithTitle("Portfolio").WithState("Doing").Build();
        var initiative = new WorkItemBuilder()
            .WithId(10).WithType("Initiative").WithTitle("Initiative").WithState("Doing").Build();
        var epic = new WorkItemBuilder()
            .WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(portfolio);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { initiative });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { epic });
        // epic at depth 2 should NOT have its children loaded when maxDepth=2
        // (children would be at depth 3, but maxDepth truncates)

        var result = await CreateWalker().WalkAsync(1, maxDepth: 2, CancellationToken.None);

        result.ShouldNotBeNull();
        CountMaxPlannableDepth(result).ShouldBe(3);
        // Even though more plannable levels exist, maxDepth prevents traversal
        result.Children![0].Children![0].Children.ShouldBeNull();
        await _repository.DidNotReceive()
            .GetChildrenAsync(100, Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────
    //  Branching hierarchies — deepest plannable path wins
    // ──────────────────────────────────────────────

    [Fact]
    public async Task BranchingHierarchy_ReportsDeepestPlannablePath()
    {
        // Epic → Feature (plannable, depth 2)
        //      → Task   (implementable, depth 1)
        // Max plannable depth should be 2 (via Epic → Feature path)
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(10).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(11).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature, task });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var result = await CreateWalker().WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        CountMaxPlannableDepth(result).ShouldBe(2);
    }

    [Fact]
    public async Task BranchingHierarchy_AsymmetricPlannableDepths()
    {
        // Portfolio → Initiative → Epic (3 plannable via left branch)
        //           → Feature          (2 plannable via right branch)
        // Max plannable depth should be 3
        var portfolio = new WorkItemBuilder()
            .WithId(1).WithType("Portfolio").WithTitle("Portfolio").WithState("Doing").Build();
        var initiative = new WorkItemBuilder()
            .WithId(10).WithType("Initiative").WithTitle("Initiative").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(11).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();
        var epic = new WorkItemBuilder()
            .WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(portfolio);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { initiative, feature });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { epic });
        _repository.GetChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var result = await CreateWalker().WalkAsync(1, maxDepth: 4, CancellationToken.None);

        result.ShouldNotBeNull();
        CountMaxPlannableDepth(result).ShouldBe(3);
    }

    // ──────────────────────────────────────────────
    //  Capabilities are faithfully annotated for budget decisions
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DeepHierarchy_AllNodesHaveCorrectCapabilities()
    {
        // Verify that each level in a deep plannable tree has the correct
        // capabilities annotated so the depth guard can make accurate decisions.
        var portfolio = new WorkItemBuilder()
            .WithId(1).WithType("Portfolio").WithTitle("Portfolio").WithState("Doing").Build();
        var initiative = new WorkItemBuilder()
            .WithId(10).WithType("Initiative").WithTitle("Initiative").WithState("Doing").Build();
        var epic = new WorkItemBuilder()
            .WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var story = new WorkItemBuilder()
            .WithId(1000).WithType("Story").WithTitle("Story").WithState("To Do").Build();
        var task = new WorkItemBuilder()
            .WithId(10000).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(portfolio);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { initiative });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { epic });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { story });
        _repository.GetChildrenAsync(1000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(10000, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var result = await CreateWalker().WalkAsync(1, maxDepth: 5, CancellationToken.None);

        result.ShouldNotBeNull();

        // Level 0: Portfolio — plannable only
        result.Capabilities.ShouldBe(["plannable"]);

        // Level 1: Initiative — plannable only
        var level1 = result.Children![0];
        level1.Capabilities.ShouldBe(["plannable"]);

        // Level 2: Epic — plannable only
        var level2 = level1.Children![0];
        level2.Capabilities.ShouldBe(["plannable"]);

        // Level 3: Story — plannable + implementable
        var level3 = level2.Children![0];
        level3.Capabilities.ShouldBe(["plannable", "implementable"]);

        // Level 4: Task — implementable only (not plannable, stops the plannable chain)
        var level4 = level3.Children![0];
        level4.Capabilities.ShouldBe(["implementable"]);

        // Total plannable depth = 4 (Portfolio, Initiative, Epic, Story)
        CountMaxPlannableDepth(result).ShouldBe(4);
    }

    [Fact]
    public async Task UnknownTypeInHierarchy_HasZeroPlannableContribution()
    {
        // Epic (plannable) → Bug (unknown type, no capabilities) → Feature (plannable)
        // Plannable depth = 2 (Epic and Feature counted; Bug is not plannable but
        // its subtree still contributes if it contains plannable children)
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var bug = new WorkItemBuilder()
            .WithId(10).WithType("Bug").WithTitle("Bug").WithState("Doing").Build();
        var feature = new WorkItemBuilder()
            .WithId(100).WithType("Feature").WithTitle("Feature").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { bug });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { feature });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var result = await CreateWalker().WalkAsync(1, maxDepth: 4, CancellationToken.None);

        result.ShouldNotBeNull();
        // Bug has empty capabilities — not plannable
        result.Children![0].Capabilities.ShouldBeEmpty();
        // Feature beneath Bug is still plannable
        result.Children[0].Children![0].Capabilities.ShouldContain("plannable");
        // Total plannable depth = 2 (Epic + Feature; Bug doesn't count)
        CountMaxPlannableDepth(result).ShouldBe(2);
    }
}
