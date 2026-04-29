using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <see cref="RouteCommand"/> using an in-memory SQLite database.
/// Tests verify the command output shape, exit codes, and JSON serialization.
/// The database is seeded with scenarios matching the coverage matrix.
/// </summary>
public sealed class RouteCommandTests : CommandTestBase
{
    [Fact]
    public void Route_ReturnsSuccessExitCode()
    {
        var cmd = new RouteCommand();
        var (exitCode, _) = CaptureConsole(() => cmd.Route(100));

        exitCode.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public void Route_OutputDeserializesToRouteResult()
    {
        var cmd = new RouteCommand();
        var (_, output) = CaptureConsole(() => cmd.Route(100));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
    }

    [Fact]
    public void Route_OutputContainsMatchingWorkItemId()
    {
        var cmd = new RouteCommand();
        var (_, output) = CaptureConsole(() => cmd.Route(42));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(42);
    }

    [Fact]
    public void Route_OutputContainsPhaseAndAction()
    {
        var cmd = new RouteCommand();
        var (_, output) = CaptureConsole(() => cmd.Route(100));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldNotBeNullOrWhiteSpace();
        result.Action.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Route_EpicWithNoChildren_ReturnsValidRouteResult()
    {
        // Seed an Epic with no children — future implementation should return needs_planning
        var epic = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Test Epic")
            .WithState("To Do")
            .Build();
        await SeedAsync(epic);

        var cmd = new RouteCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Route(100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(100);
        result.Phase.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Route_TaskInProgress_ReturnsValidRouteResult()
    {
        // Seed a Task in InProgress state — future implementation should return in_progress
        var task = new WorkItemBuilder()
            .WithId(200)
            .WithType("Task")
            .WithTitle("Test Task")
            .WithState("Doing")
            .Build();
        await SeedAsync(task);

        var cmd = new RouteCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Route(200));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(200);
    }

    [Fact]
    public async Task Route_SeededDatabase_WorkItemCanBeQueried()
    {
        // Verify the in-memory SQLite infrastructure works end-to-end
        var epic = new WorkItemBuilder()
            .WithId(300)
            .WithType("Epic")
            .WithTitle("Infra Test")
            .WithState("To Do")
            .Build();
        await SeedAsync(epic);

        var loaded = await Repository.GetByIdAsync(300);
        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(300);
        loaded.Title.ShouldBe("Infra Test");
        loaded.State.ShouldBe("To Do");
    }

    [Fact]
    public async Task Route_SeededEpicWithChildren_ChildrenCanBeQueried()
    {
        // Verify parent-child relationships in the in-memory database
        var (epic, children) = new WorkItemBuilder()
            .WithId(400)
            .WithType("Epic")
            .WithTitle("Parent Epic")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(401).WithType("Task").WithTitle("Child 1").WithState("To Do"),
                new WorkItemBuilder().WithId(402).WithType("Task").WithTitle("Child 2").WithState("Doing"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var loadedChildren = await Repository.GetChildrenAsync(400);
        loadedChildren.Count.ShouldBe(2);
    }
}
