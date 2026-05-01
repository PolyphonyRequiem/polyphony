using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end cross-process template tests for <see cref="RouteCommand"/>.
/// Exercises the full pipeline (work item seeding → in-memory SQLite → RouteCommand → JSON output)
/// for each of the 4 process templates (Basic, Agile, Scrum, CMMI).
/// </summary>
public sealed class CrossProcessRouteCommandTests : CommandTestBase
{
    /// <summary>
    /// Defines the hierarchy structure and state names for a single process template.
    /// </summary>
    private sealed record TemplateDefinition(
        string Name,
        string TopType,
        string MiddleType,
        string[] MiddleCapabilities,
        string LeafType,
        string ProposedState,
        string InProgressState,
        string CompletedState);

    private static readonly TemplateDefinition[] Templates =
    [
        new("Basic", "Epic", "Issue", ["plannable", "implementable"], "Task", "To Do", "Doing", "Done"),
        new("Agile", "Epic", "User Story", ["plannable", "implementable"], "Task", "New", "Active", "Closed"),
        new("Scrum", "Epic", "Product Backlog Item", ["plannable", "implementable"], "Task", "New", "Committed", "Done"),
        new("CMMI", "Epic", "Requirement", ["plannable", "implementable"], "Task", "Proposed", "Active", "Closed"),
    ];

    public static IEnumerable<object[]> AllTemplateNames =>
        Templates.Select(t => new object[] { t.Name });

    private static TemplateDefinition GetTemplate(string name) =>
        Templates.First(t => t.Name == name);

    private RouteCommand CreateCommand(TemplateDefinition template)
    {
        var config = new ProcessConfigBuilder()
            .WithProcessTemplate(template.Name)
            .WithType(template.TopType, ["plannable"])
            .WithType(template.MiddleType, template.MiddleCapabilities)
            .WithType(template.LeafType, ["implementable"])
            .WithBranchStrategy()
            .Build();
        return new RouteCommand(new PhaseDetector(config), Repository, config);
    }

    // --- Scenario 1: Top-level plannable in Proposed → NeedsPlanning ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_TopLevelInProposed_ReturnsNeedsPlanning(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(1000)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic Proposed")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1000));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1000);
        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        result.Action.ShouldBe(SdlcAction.Plan);
    }

    // --- Scenario 2: Top-level in progress with mixed children → InProgress ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_TopLevelWithMixedChildren_ReturnsInProgress(string templateName)
    {
        var t = GetTemplate(templateName);
        var (epic, children) = new WorkItemBuilder()
            .WithId(1100)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic Mixed")
            .WithState(t.InProgressState)
            .WithChildren(
                new WorkItemBuilder().WithId(1101).WithType(t.MiddleType).WithTitle("Done Child").WithState(t.CompletedState),
                new WorkItemBuilder().WithId(1102).WithType(t.MiddleType).WithTitle("Doing Child").WithState(t.InProgressState))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1100);
        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    // --- Scenario 3: All children completed → ReadyForCompletion ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_TopLevelAllChildrenDone_ReturnsReadyForCompletion(string templateName)
    {
        var t = GetTemplate(templateName);
        var (epic, children) = new WorkItemBuilder()
            .WithId(1200)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic AllDone")
            .WithState(t.InProgressState)
            .WithChildren(
                new WorkItemBuilder().WithId(1201).WithType(t.MiddleType).WithTitle("Done 1").WithState(t.CompletedState),
                new WorkItemBuilder().WithId(1202).WithType(t.MiddleType).WithTitle("Done 2").WithState(t.CompletedState))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1200));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1200);
        result.Phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        result.Action.ShouldBe(SdlcAction.Close);
    }

    // --- Scenario 4: Completed top-level → Done ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_CompletedTopLevel_ReturnsDone(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(1300)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic Done")
            .WithState(t.CompletedState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1300));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1300);
        result.Phase.ShouldBe(SdlcPhase.Done);
        result.Action.ShouldBe(SdlcAction.None);
    }

    // --- Scenario 5: Middle-tier plannable+implementable in proposed → NeedsPlanning ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_MiddleTierInProposed_ReturnsNeedsPlanning(string templateName)
    {
        var t = GetTemplate(templateName);
        var middle = new WorkItemBuilder()
            .WithId(1400)
            .WithType(t.MiddleType)
            .WithTitle($"{templateName} Middle Proposed")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(middle);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1400));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1400);
        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        result.Action.ShouldBe(SdlcAction.Plan);
    }

    // --- Scenario 6: Middle-tier in progress with no children → ReadyForImplementation (plannable+implementable) ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_MiddleTierInProgressNoChildren_ReturnsReadyForImplementation(string templateName)
    {
        var t = GetTemplate(templateName);
        var middle = new WorkItemBuilder()
            .WithId(1500)
            .WithType(t.MiddleType)
            .WithTitle($"{templateName} Middle InProgress")
            .WithState(t.InProgressState)
            .Build();
        await SeedAsync(middle);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1500));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1500);
        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    // --- Scenario 7: Leaf implementable in proposed → ReadyForImplementation ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_LeafInProposed_ReturnsReadyForImplementation(string templateName)
    {
        var t = GetTemplate(templateName);
        var task = new WorkItemBuilder()
            .WithId(1600)
            .WithType(t.LeafType)
            .WithTitle($"{templateName} Task Proposed")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1600));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1600);
        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }

    // --- Scenario 8: Leaf implementable in progress → InProgress ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_LeafInProgress_ReturnsInProgress(string templateName)
    {
        var t = GetTemplate(templateName);
        var task = new WorkItemBuilder()
            .WithId(1700)
            .WithType(t.LeafType)
            .WithTitle($"{templateName} Task InProgress")
            .WithState(t.InProgressState)
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1700));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1700);
        result.Phase.ShouldBe(SdlcPhase.InProgress);
        result.Action.ShouldBe(SdlcAction.Monitor);
    }

    // --- Scenario 9: Leaf completed → Done ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_LeafCompleted_ReturnsDone(string templateName)
    {
        var t = GetTemplate(templateName);
        var task = new WorkItemBuilder()
            .WithId(1800)
            .WithType(t.LeafType)
            .WithTitle($"{templateName} Task Done")
            .WithState(t.CompletedState)
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(1800));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1800);
        result.Phase.ShouldBe(SdlcPhase.Done);
        result.Action.ShouldBe(SdlcAction.None);
    }

    // --- Scenario 10: JSON output uses snake_case per PolyphonyJsonContext ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_OutputUsesSnakeCasePropertyNames(string templateName)
    {
        var t = GetTemplate(templateName);
        var task = new WorkItemBuilder()
            .WithId(1900)
            .WithType(t.LeafType)
            .WithTitle($"{templateName} Snake Case")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand(t);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(1900));

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"phase\"");
        output.ShouldContain("\"action\"");
    }

    // --- Scenario 11: Exit codes are ExitCodes.Success for all valid routes ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_AllValidRoutes_ReturnSuccessExitCode(string templateName)
    {
        var t = GetTemplate(templateName);
        var items = new[]
        {
            new WorkItemBuilder().WithId(2001).WithType(t.TopType).WithTitle("Proposed").WithState(t.ProposedState).Build(),
            new WorkItemBuilder().WithId(2002).WithType(t.LeafType).WithTitle("InProgress").WithState(t.InProgressState).Build(),
            new WorkItemBuilder().WithId(2003).WithType(t.LeafType).WithTitle("Completed").WithState(t.CompletedState).Build(),
        };
        await SeedAsync(items);

        var cmd = CreateCommand(t);

        foreach (var item in items)
        {
            var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Route(item.Id));
            exitCode.ShouldBe(ExitCodes.Success, $"Failed for {templateName} item {item.Id} in state {item.State}");
        }
    }

    // --- Scenario 12: Workspace hint present with branch strategy ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_OutputContainsWorkspaceHint(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(2100)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Branch Test")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(2100));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkspaceHint.ShouldNotBeNull();
        result.WorkspaceHint!.FeatureBranch.ShouldNotBeNull();
        result.WorkspaceHint.FeatureBranch!.ShouldContain("2100");
    }

    // --- Scenario 13: Middle-tier with all children done → ReadyForCompletion ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_MiddleTierAllChildrenDone_ReturnsReadyForCompletion(string templateName)
    {
        var t = GetTemplate(templateName);
        var (middle, children) = new WorkItemBuilder()
            .WithId(2200)
            .WithType(t.MiddleType)
            .WithTitle($"{templateName} Middle AllDone")
            .WithState(t.InProgressState)
            .WithChildren(
                new WorkItemBuilder().WithId(2201).WithType(t.LeafType).WithTitle("Done 1").WithState(t.CompletedState),
                new WorkItemBuilder().WithId(2202).WithType(t.LeafType).WithTitle("Done 2").WithState(t.CompletedState))
            .BuildAll();
        await SeedAsync([middle, .. children]);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(2200));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(2200);
        result.Phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        result.Action.ShouldBe(SdlcAction.Close);
    }

    // --- Scenario 14: Top-level in progress with no children → NeedsSeeding (plannable-only) ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_TopLevelInProgressNoChildren_ReturnsNeedsSeeding(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(2300)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic NeedsSeed")
            .WithState(t.InProgressState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(2300));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(2300);
        result.Phase.ShouldBe(SdlcPhase.NeedsSeeding);
        result.Action.ShouldBe(SdlcAction.Seed);
    }

    // --- Scenario 15: Top-level with all children proposed → ReadyForImplementation ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Route_TopLevelAllChildrenProposed_ReturnsReadyForImplementation(string templateName)
    {
        var t = GetTemplate(templateName);
        var (epic, children) = new WorkItemBuilder()
            .WithId(2400)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic AllProposed")
            .WithState(t.InProgressState)
            .WithChildren(
                new WorkItemBuilder().WithId(2401).WithType(t.MiddleType).WithTitle("Proposed 1").WithState(t.ProposedState),
                new WorkItemBuilder().WithId(2402).WithType(t.MiddleType).WithTitle("Proposed 2").WithState(t.ProposedState))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(2400));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(2400);
        result.Phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        result.Action.ShouldBe(SdlcAction.Implement);
    }
}
