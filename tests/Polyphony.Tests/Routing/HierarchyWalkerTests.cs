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
/// Unit tests for <see cref="HierarchyWalker"/> using NSubstitute mocks.
/// Covers depth limiting, capability annotation, missing items, and multi-level trees.
/// </summary>
public sealed class HierarchyWalkerTests
{
    private readonly IWorkItemRepository _repository = Substitute.For<IWorkItemRepository>();

    private readonly ProcessConfig _config = new ProcessConfigBuilder()
        .WithType("Epic", ["plannable"])
        .WithType("Issue", ["plannable", "implementable"])
        .WithType("Task", ["implementable"])
        .Build();

    private HierarchyWalker CreateWalker() => new(_config, _repository);

    [Fact]
    public async Task WalkAsync_MissingRoot_ReturnsNull()
    {
        _repository.GetByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var walker = CreateWalker();
        var result = await walker.WalkAsync(999, maxDepth: 3, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task WalkAsync_DepthZero_ReturnsRootOnly()
    {
        var root = new WorkItemBuilder()
            .WithId(1)
            .WithType("Epic")
            .WithTitle("Root Epic")
            .WithState("Doing")
            .Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(root);

        var walker = CreateWalker();
        var result = await walker.WalkAsync(1, maxDepth: 0, CancellationToken.None);

        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1);
        result.Title.ShouldBe("Root Epic");
        result.Type.ShouldBe("Epic");
        result.State.ShouldBe("Doing");
        result.Children.ShouldBeNull();
    }

    [Fact]
    public async Task WalkAsync_DepthZero_DoesNotLoadChildren()
    {
        var root = new WorkItemBuilder()
            .WithId(1)
            .WithType("Epic")
            .WithTitle("Root Epic")
            .WithState("Doing")
            .Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(root);

        var walker = CreateWalker();
        await walker.WalkAsync(1, maxDepth: 0, CancellationToken.None);

        await _repository.DidNotReceive().GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WalkAsync_DepthOne_IncludesDirectChildren()
    {
        var root = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Root").WithState("Doing").Build();
        var child1 = new WorkItemBuilder()
            .WithId(10).WithType("Issue").WithTitle("Child Issue").WithState("To Do").Build();
        var child2 = new WorkItemBuilder()
            .WithId(11).WithType("Task").WithTitle("Child Task").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(root);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { child1, child2 });

        var walker = CreateWalker();
        var result = await walker.WalkAsync(1, maxDepth: 1, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children.Length.ShouldBe(2);
        result.Children[0].WorkItemId.ShouldBe(10);
        result.Children[0].Type.ShouldBe("Issue");
        result.Children[1].WorkItemId.ShouldBe(11);
        result.Children[1].Type.ShouldBe("Task");
    }

    [Fact]
    public async Task WalkAsync_DepthOne_ChildrenHaveNoChildren()
    {
        var root = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Root").WithState("Doing").Build();
        var child = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithTitle("Child").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(root);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { child });

        var walker = CreateWalker();
        var result = await walker.WalkAsync(1, maxDepth: 1, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children[0].Children.ShouldBeNull();

        // Should not try to load grandchildren at depth 1
        await _repository.DidNotReceive().GetChildrenAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WalkAsync_ThreeLevelTree_ReturnsFullHierarchy()
    {
        // Epic (1) → Issue (10) → Task (100)
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(10).WithType("Issue").WithTitle("Issue").WithState("Doing").Build();
        var task = new WorkItemBuilder()
            .WithId(100).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue });
        _repository.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { task });
        _repository.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker();
        var result = await walker.WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1);

        result.Children.ShouldNotBeNull();
        result.Children.Length.ShouldBe(1);
        result.Children[0].WorkItemId.ShouldBe(10);

        result.Children[0].Children.ShouldNotBeNull();
        result.Children[0].Children!.Length.ShouldBe(1);
        result.Children[0].Children![0].WorkItemId.ShouldBe(100);
    }

    [Fact]
    public async Task WalkAsync_AnnotatesCapabilitiesFromProcessConfig()
    {
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(10).WithType("Issue").WithTitle("Issue").WithState("To Do").Build();
        var task = new WorkItemBuilder()
            .WithId(100).WithType("Task").WithTitle("Task").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue, task });

        var walker = CreateWalker();
        var result = await walker.WalkAsync(1, maxDepth: 1, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Capabilities.ShouldBe(["plannable"]);

        result.Children.ShouldNotBeNull();
        result.Children[0].Capabilities.ShouldBe(["plannable", "implementable"]);
        result.Children[1].Capabilities.ShouldBe(["implementable"]);
    }

    [Fact]
    public async Task WalkAsync_UnknownType_ReturnsEmptyCapabilities()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Bug").WithTitle("A Bug").WithState("New").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var walker = CreateWalker();
        var result = await walker.WalkAsync(1, maxDepth: 0, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Type.ShouldBe("Bug");
        result.Capabilities.ShouldBeEmpty();
    }

    [Fact]
    public async Task WalkAsync_LeafNodeWithNoChildren_ChildrenIsNull()
    {
        var leaf = new WorkItemBuilder()
            .WithId(1).WithType("Task").WithTitle("Leaf").WithState("To Do").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(leaf);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var walker = CreateWalker();
        var result = await walker.WalkAsync(1, maxDepth: 3, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Children.ShouldBeNull();
    }

    [Fact]
    public async Task WalkAsync_DepthTruncatesAtMaxDepth()
    {
        // 3-level tree: Epic → Issue → Task, but maxDepth=1 should cut at Issue
        var epic = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Epic").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(10).WithType("Issue").WithTitle("Issue").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(epic);
        _repository.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { issue });

        var walker = CreateWalker();
        var result = await walker.WalkAsync(1, maxDepth: 1, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children[0].Children.ShouldBeNull();

        // Should not try to load grandchildren
        await _repository.DidNotReceive().GetChildrenAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WalkAsync_CancellationRespected()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var root = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithTitle("Root").WithState("Doing").Build();

        _repository.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return root;
            });

        var walker = CreateWalker();
        await Should.ThrowAsync<OperationCanceledException>(
            () => walker.WalkAsync(1, maxDepth: 3, cts.Token));
    }
}
