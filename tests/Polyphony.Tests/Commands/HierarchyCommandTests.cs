using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <see cref="HierarchyCommand"/> using an in-memory SQLite database.
/// Tests verify JSON tree output, depth limits, facet annotations, error handling,
/// and that children arrays are never null in the output.
/// </summary>
public sealed class HierarchyCommandTests : CommandTestBase
{
    private HierarchyCommand CreateCommand() => new(new HierarchyWalker(Config, Repository));

    [Fact]
    public async Task Hierarchy_RootNotFound_ReturnsCacheErrorExitCode()
    {
        var cmd = CreateCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Hierarchy(999));

        exitCode.ShouldBe(ExitCodes.CacheError);
    }

    [Fact]
    public async Task Hierarchy_RootNotFound_OutputsErrorJson()
    {
        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(999));

        output.ShouldContain("\"error\"");
        output.ShouldContain("\"work_item_id\":999");
    }

    [Fact]
    public async Task Hierarchy_SingleItem_ReturnsSuccessWithEmptyChildren()
    {
        var epic = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Root Epic")
            .WithState("Doing")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(100);
        result.Title.ShouldBe("Root Epic");
        result.Type.ShouldBe("Epic");
        result.State.ShouldBe("Doing");
        result.Children.ShouldNotBeNull();
        result.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hierarchy_SingleItem_AnnotatesFacets()
    {
        var epic = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Root Epic")
            .WithState("Doing")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(100));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Facets.ShouldContain("plannable");
    }

    [Fact]
    public async Task Hierarchy_TwoLevelTree_ChildrenIncludedInOutput()
    {
        var (epic, children) = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Parent Epic")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(201).WithType("Issue").WithTitle("Child Issue").WithState("To Do"),
                new WorkItemBuilder().WithId(202).WithType("Task").WithTitle("Child Task").WithState("Doing"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(100);
        result.Children.ShouldNotBeNull();
        result.Children.Length.ShouldBe(2);
        result.Children[0].WorkItemId.ShouldBe(201);
        result.Children[1].WorkItemId.ShouldBe(202);
    }

    [Fact]
    public async Task Hierarchy_TwoLevelTree_LeafChildrenHaveEmptyArray()
    {
        var (epic, children) = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Parent Epic")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(201).WithType("Task").WithTitle("Leaf Task").WithState("To Do"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(100));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children[0].Children.ShouldNotBeNull();
        result.Children[0].Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hierarchy_DepthLimit_TruncatesAtSpecifiedDepth()
    {
        // 3-level tree: Epic → Issue → Task, but request depth=1
        var epic = new WorkItemBuilder()
            .WithId(300).WithType("Epic").WithTitle("Depth Test Epic").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(301).WithType("Issue").WithTitle("Issue").WithState("Doing").WithParentId(300).Build();
        var task = new WorkItemBuilder()
            .WithId(302).WithType("Task").WithTitle("Task").WithState("To Do").WithParentId(301).Build();
        await SeedAsync(epic, issue, task);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(300, depth: 1));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children!.Length.ShouldBe(1);
        // At depth 1, the child's children should be an empty array (not traversed)
        result.Children[0].Children.ShouldNotBeNull();
        result.Children[0].Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hierarchy_DefaultDepth_TraversesThreeLevels()
    {
        // 3-level tree: Epic → Issue → Task; default depth=3 should include all
        var epic = new WorkItemBuilder()
            .WithId(400).WithType("Epic").WithTitle("Full Tree").WithState("Doing").Build();
        var issue = new WorkItemBuilder()
            .WithId(401).WithType("Issue").WithTitle("Issue").WithState("Doing").WithParentId(400).Build();
        var task = new WorkItemBuilder()
            .WithId(402).WithType("Task").WithTitle("Task").WithState("To Do").WithParentId(401).Build();
        await SeedAsync(epic, issue, task);

        var cmd = CreateCommand();
        // depth not specified — should use default of 3
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(400));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children![0].Children.ShouldNotBeNull();
        result.Children[0].Children![0].WorkItemId.ShouldBe(402);
    }

    [Fact]
    public async Task Hierarchy_OutputUsesSnakeCasePropertyNames()
    {
        var epic = new WorkItemBuilder()
            .WithId(500)
            .WithType("Epic")
            .WithTitle("Snake Case Test")
            .WithState("Doing")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(500));

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"children\"");
        output.ShouldContain("\"facets\"");
    }

    [Fact]
    public async Task Hierarchy_ItemWithTags_OutputIncludesTagsField()
    {
        var epic = new WorkItemBuilder()
            .WithId(600).WithType("Epic").WithTitle("Tagged Epic")
            .WithState("Doing").WithTags("PG-1; Sprint 5")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(600));

        exitCode.ShouldBe(ExitCodes.Success);
        output.ShouldContain("\"tags\"");
        output.ShouldContain("PG-1; Sprint 5");
    }

    [Fact]
    public async Task Hierarchy_ItemWithoutTags_OutputOmitsTagsField()
    {
        var epic = new WorkItemBuilder()
            .WithId(700).WithType("Epic").WithTitle("Untagged")
            .WithState("Doing")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(700));

        output.ShouldNotContain("\"tags\"");
    }
}


