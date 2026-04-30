using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <see cref="RouteCommand"/> using an in-memory SQLite database.
/// Tests verify the command output shape, exit codes, phase detection, workspace hints,
/// and error handling for missing work items.
/// </summary>
public sealed class RouteCommandTests : CommandTestBase
{
    private RouteCommand CreateCommand() => new(new PhaseDetector(Config), Repository, Config);

    [Fact]
    public async Task Route_WorkItemNotFound_ReturnsCacheErrorExitCode()
    {
        var cmd = CreateCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Route(999));

        exitCode.ShouldBe(ExitCodes.CacheError);
    }

    [Fact]
    public async Task Route_WorkItemNotFound_OutputsErrorJson()
    {
        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(999));

        output.ShouldContain("\"error\"");
        output.ShouldContain("\"work_item_id\":999");
    }

    [Fact]
    public async Task Route_EpicInProposed_ReturnsNeedsPlanning()
    {
        var epic = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Test Epic")
            .WithState("To Do")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(100);
        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        result.Action.ShouldBe(SdlcAction.Plan);
    }

    [Fact]
    public async Task Route_EpicInProgressNoChildren_ReturnsNeedsSeeding()
    {
        var epic = new WorkItemBuilder()
            .WithId(101)
            .WithType("Epic")
            .WithTitle("Epic In Progress")
            .WithState("Doing")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(101));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.NeedsSeeding);
        result.Action.ShouldBe(SdlcAction.Seed);
    }

    [Fact]
    public async Task Route_EpicWithAllProposedChildren_ReturnsReadyForImplementation()
    {
        var (epic, children) = new WorkItemBuilder()
            .WithId(102)
            .WithType("Epic")
            .WithTitle("Epic With Tasks")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(201).WithType("Task").WithTitle("Task 1").WithState("To Do"),
                new WorkItemBuilder().WithId(202).WithType("Task").WithTitle("Task 2").WithState("To Do"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(102));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    [Fact]
    public async Task Route_EpicWithMixedChildren_ReturnsInProgress()
    {
        var (epic, children) = new WorkItemBuilder()
            .WithId(103)
            .WithType("Epic")
            .WithTitle("Mixed Epic")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(203).WithType("Task").WithTitle("Done Task").WithState("Done"),
                new WorkItemBuilder().WithId(204).WithType("Task").WithTitle("In Prog Task").WithState("Doing"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(103));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    [Fact]
    public async Task Route_EpicWithAllCompletedChildren_ReturnsReadyForCompletion()
    {
        var (epic, children) = new WorkItemBuilder()
            .WithId(104)
            .WithType("Epic")
            .WithTitle("Completed Epic")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(205).WithType("Task").WithTitle("Task 1").WithState("Done"),
                new WorkItemBuilder().WithId(206).WithType("Task").WithTitle("Task 2").WithState("Done"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(104));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        result.Action.ShouldBe(SdlcAction.Close);
    }

    [Fact]
    public async Task Route_TaskInProposed_ReturnsReadyForImplementation()
    {
        var task = new WorkItemBuilder()
            .WithId(200)
            .WithType("Task")
            .WithTitle("Test Task")
            .WithState("To Do")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(200));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    [Fact]
    public async Task Route_TaskInProgress_ReturnsInProgress()
    {
        var task = new WorkItemBuilder()
            .WithId(210)
            .WithType("Task")
            .WithTitle("Doing Task")
            .WithState("Doing")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(210));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    [Fact]
    public async Task Route_CompletedItem_ReturnsDone()
    {
        var task = new WorkItemBuilder()
            .WithId(220)
            .WithType("Task")
            .WithTitle("Done Task")
            .WithState("Done")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(220));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.Done);
        result.Action.ShouldBe(SdlcAction.None);
    }

    [Fact]
    public async Task Route_RemovedItem_ReturnsRemoved()
    {
        var task = new WorkItemBuilder()
            .WithId(230)
            .WithType("Task")
            .WithTitle("Removed Task")
            .WithState("Removed")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(230));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.Removed);
        result.Action.ShouldBe(SdlcAction.None);
    }

    [Fact]
    public async Task Route_OutputContainsWorkspaceHint()
    {
        var epic = new WorkItemBuilder()
            .WithId(300)
            .WithType("Epic")
            .WithTitle("Branch Test Epic")
            .WithState("To Do")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(300));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkspaceHint.ShouldNotBeNull();
        result.WorkspaceHint!.FeatureBranch.ShouldNotBeNull();
        result.WorkspaceHint.FeatureBranch!.ShouldContain("300");
    }

    [Fact]
    public async Task Route_OutputUsesSnakeCasePropertyNames()
    {
        var task = new WorkItemBuilder()
            .WithId(400)
            .WithType("Task")
            .WithTitle("Snake Case")
            .WithState("To Do")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(400));

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"phase\"");
        output.ShouldContain("\"action\"");
    }

    [Fact]
    public async Task Route_IssueInProposedNoChildren_ReturnsNeedsPlanning()
    {
        // Issue is plannable + implementable; in Proposed → needs_planning
        var issue = new WorkItemBuilder()
            .WithId(500)
            .WithType("Issue")
            .WithTitle("Test Issue")
            .WithState("To Do")
            .Build();
        await SeedAsync(issue);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(500));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        result.Action.ShouldBe(SdlcAction.Plan);
    }

    [Fact]
    public async Task Route_IssueInProgressNoChildren_ReturnsReadyForImplementation()
    {
        // Issue is plannable + implementable; in InProgress with no children → direct implementation
        var issue = new WorkItemBuilder()
            .WithId(501)
            .WithType("Issue")
            .WithTitle("Impl Issue")
            .WithState("Doing")
            .Build();
        await SeedAsync(issue);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(501));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    [Fact]
    public async Task Route_SeededDatabase_WorkItemCanBeQueried()
    {
        var epic = new WorkItemBuilder()
            .WithId(600)
            .WithType("Epic")
            .WithTitle("Infra Test")
            .WithState("To Do")
            .Build();
        await SeedAsync(epic);

        var loaded = await Repository.GetByIdAsync(600);
        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(600);
        loaded.Title.ShouldBe("Infra Test");
        loaded.State.ShouldBe("To Do");
    }

    [Fact]
    public async Task Route_SeededEpicWithChildren_ChildrenCanBeQueried()
    {
        var (epic, children) = new WorkItemBuilder()
            .WithId(700)
            .WithType("Epic")
            .WithTitle("Parent Epic")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(701).WithType("Task").WithTitle("Child 1").WithState("To Do"),
                new WorkItemBuilder().WithId(702).WithType("Task").WithTitle("Child 2").WithState("Doing"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var loadedChildren = await Repository.GetChildrenAsync(700);
        loadedChildren.Count.ShouldBe(2);
    }
}
