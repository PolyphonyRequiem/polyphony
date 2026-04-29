using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <see cref="HierarchyCommand"/> using an in-memory SQLite database.
/// Tests verify JSON tree output, depth limits, and capability annotations.
/// The database is seeded with multi-level hierarchies matching the coverage matrix.
/// </summary>
public sealed class HierarchyCommandTests : CommandTestBase
{
    [Fact]
    public void Hierarchy_ReturnsSuccessExitCode()
    {
        var cmd = new HierarchyCommand();
        var (exitCode, _) = CaptureConsole(() => cmd.Hierarchy(100));

        exitCode.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public void Hierarchy_OutputDeserializesToHierarchyResult()
    {
        var cmd = new HierarchyCommand();
        var (_, output) = CaptureConsole(() => cmd.Hierarchy(100));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
    }

    [Fact]
    public void Hierarchy_OutputContainsMatchingWorkItemId()
    {
        var cmd = new HierarchyCommand();
        var (_, output) = CaptureConsole(() => cmd.Hierarchy(42));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(42);
    }

    [Fact]
    public void Hierarchy_OutputContainsTypeAndState()
    {
        var cmd = new HierarchyCommand();
        var (_, output) = CaptureConsole(() => cmd.Hierarchy(100));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Type.ShouldNotBeNullOrWhiteSpace();
        result.State.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Hierarchy_OutputContainsCapabilitiesArray()
    {
        var cmd = new HierarchyCommand();
        var (_, output) = CaptureConsole(() => cmd.Hierarchy(100));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Capabilities.ShouldNotBeNull();
    }

    [Fact]
    public async Task Hierarchy_TwoLevelTree_DatabaseContainsHierarchy()
    {
        // Seed a 2-level tree: Epic → Issue + Task
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

        // Verify the in-memory hierarchy is queryable
        var loadedRoot = await Repository.GetByIdAsync(100);
        loadedRoot.ShouldNotBeNull();

        var loadedChildren = await Repository.GetChildrenAsync(100);
        loadedChildren.Count.ShouldBe(2);

        // Run the command against the seeded item
        var cmd = new HierarchyCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Hierarchy(100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(100);
    }

    [Fact]
    public async Task Hierarchy_DepthLimit_ReturnsValidJson()
    {
        // Seed a 2-level tree and request depth=1
        var (epic, children) = new WorkItemBuilder()
            .WithId(300)
            .WithType("Epic")
            .WithTitle("Depth Test Epic")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(301).WithType("Task").WithTitle("Task 1").WithState("To Do"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = new HierarchyCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Hierarchy(300, depth: 1));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(300);
    }

    [Fact]
    public async Task Hierarchy_SeededDatabase_ParentChildRelationshipPreserved()
    {
        var parent = new WorkItemBuilder()
            .WithId(500)
            .WithType("Epic")
            .WithTitle("Root")
            .WithState("Doing")
            .Build();
        var child = new WorkItemBuilder()
            .WithId(501)
            .WithType("Task")
            .WithTitle("Leaf")
            .WithState("To Do")
            .WithParentId(500)
            .Build();
        await SeedAsync(parent, child);

        var loadedChild = await Repository.GetByIdAsync(501);
        loadedChild.ShouldNotBeNull();
        loadedChild.ParentId.ShouldBe(500);
    }
}
