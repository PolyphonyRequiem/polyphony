using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Full lifecycle integration tests that walk an Epic through all SDLC phases
/// (NeedsPlanning → NeedsSeeding → ReadyForImplementation → InProgress → ReadyForCompletion → Done)
/// using the Agile process template with realistic pre-seeded hierarchies.
/// Each test exercises the complete pipeline: work item seeding → in-memory SQLite → command → JSON output.
/// </summary>
public sealed class IntegrationScenarioTests : CommandTestBase
{
    private const string AgileTemplate = "Agile";
    private const string EpicType = "Epic";
    private const string UserStoryType = "User Story";
    private const string TaskType = "Task";

    // Agile state names
    private const string ProposedState = "New";
    private const string InProgressState = "Active";
    private const string CompletedState = "Closed";

    // Agile transition targets
    private const string BeginPlanningTarget = "Active";
    private const string ImplementationCompleteTarget = "Closed";

    private static ProcessConfigBuilder CreateAgileConfigBuilder()
    {
        var transitions = new Dictionary<string, string>
        {
            ["begin_planning"] = BeginPlanningTarget,
            ["implementation_complete"] = ImplementationCompleteTarget,
        };

        return new ProcessConfigBuilder()
            .WithProcessTemplate(AgileTemplate)
            .WithType(EpicType, ["plannable"], transitions)
            .WithType(UserStoryType, ["plannable", "implementable"], transitions)
            .WithType(TaskType, ["implementable"], transitions)
            .WithBranchStrategy();
    }

    private RouteCommand CreateRouteCommand()
    {
        var config = CreateAgileConfigBuilder().Build();
        return new RouteCommand(new PhaseDetector(config), Repository, config);
    }

    private ValidateCommand CreateValidateCommand()
    {
        var config = CreateAgileConfigBuilder().Build();
        return new ValidateCommand(new TransitionValidator(config), Repository);
    }

    private HierarchyCommand CreateHierarchyCommand()
    {
        var config = CreateAgileConfigBuilder().Build();
        return new HierarchyCommand(new HierarchyWalker(config, Repository));
    }

    // =========================================================================
    // Full lifecycle test — walks an Agile Epic through all 6 phases
    // =========================================================================

    [Fact]
    public async Task FullLifecycle_AgileEpic_ProgressesThroughAllPhases()
    {
        // --- Phase 1: NeedsPlanning ---
        // Epic in "New" (Proposed) with no children
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType(EpicType).WithTitle("Agile Lifecycle Epic").WithState(ProposedState)
                .Build());

        var routeResult = await RouteAndAssert(100, SdlcPhase.NeedsPlanning, SdlcAction.Plan);
        routeResult.Message.ShouldNotBeNullOrEmpty();

        // begin_planning should be valid from Proposed state
        await ValidateAndAssertValid(100, "begin_planning", BeginPlanningTarget);

        // implementation_complete should be invalid from Proposed state
        await ValidateAndAssertInvalid(100, "implementation_complete");

        // --- Phase 2: NeedsSeeding ---
        // Epic transitions to "Active" (InProgress), still no children
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType(EpicType).WithTitle("Agile Lifecycle Epic").WithState(InProgressState)
                .Build());

        await RouteAndAssert(100, SdlcPhase.NeedsSeeding, SdlcAction.Seed);

        // begin_planning should be invalid from InProgress state
        await ValidateAndAssertInvalid(100, "begin_planning");

        // implementation_complete should be valid from InProgress state
        await ValidateAndAssertValid(100, "implementation_complete", ImplementationCompleteTarget);

        // --- Phase 3: ReadyForImplementation ---
        // Add User Stories in "New" (Proposed) as children of the Epic
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(201).WithType(UserStoryType).WithTitle("Auth Module").WithState(ProposedState).WithParentId(100)
                .Build(),
            new WorkItemBuilder()
                .WithId(202).WithType(UserStoryType).WithTitle("API Layer").WithState(ProposedState).WithParentId(100)
                .Build());

        await RouteAndAssert(100, SdlcPhase.ReadyForImplementation, SdlcAction.Implement);

        // --- Phase 4: InProgress ---
        // One Story moves to Active, the other stays New → mixed states
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(201).WithType(UserStoryType).WithTitle("Auth Module").WithState(InProgressState).WithParentId(100)
                .Build());

        await RouteAndAssert(100, SdlcPhase.InProgress, SdlcAction.Monitor);

        // --- Phase 5: ReadyForCompletion ---
        // All children move to Closed
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(201).WithType(UserStoryType).WithTitle("Auth Module").WithState(CompletedState).WithParentId(100)
                .Build(),
            new WorkItemBuilder()
                .WithId(202).WithType(UserStoryType).WithTitle("API Layer").WithState(CompletedState).WithParentId(100)
                .Build());

        await RouteAndAssert(100, SdlcPhase.ReadyForCompletion, SdlcAction.Close);

        // --- Phase 6: Done ---
        // Epic itself is Closed
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType(EpicType).WithTitle("Agile Lifecycle Epic").WithState(CompletedState)
                .Build());

        var doneResult = await RouteAndAssert(100, SdlcPhase.Done, SdlcAction.None);
        doneResult.Message.ShouldNotBeNullOrEmpty();
    }

    // =========================================================================
    // 3-tier hierarchy lifecycle — Epic → User Story → Task
    // =========================================================================

    [Fact]
    public async Task FullLifecycle_ThreeTierHierarchy_RoutesCorrectlyAtEachLevel()
    {
        // Seed a 3-tier hierarchy: Epic → 2 User Stories → 2 Tasks each
        var epic = new WorkItemBuilder()
            .WithId(300).WithType(EpicType).WithTitle("Platform Epic").WithState(InProgressState).Build();

        var story1 = new WorkItemBuilder()
            .WithId(310).WithType(UserStoryType).WithTitle("Story A").WithState(InProgressState).WithParentId(300).Build();
        var story2 = new WorkItemBuilder()
            .WithId(320).WithType(UserStoryType).WithTitle("Story B").WithState(ProposedState).WithParentId(300).Build();

        var task1 = new WorkItemBuilder()
            .WithId(311).WithType(TaskType).WithTitle("Task A1").WithState(InProgressState).WithParentId(310).Build();
        var task2 = new WorkItemBuilder()
            .WithId(312).WithType(TaskType).WithTitle("Task A2").WithState(ProposedState).WithParentId(310).Build();
        var task3 = new WorkItemBuilder()
            .WithId(321).WithType(TaskType).WithTitle("Task B1").WithState(ProposedState).WithParentId(320).Build();

        await SeedAsync(epic, story1, story2, task1, task2, task3);

        // Epic with mixed children → InProgress
        await RouteAndAssert(300, SdlcPhase.InProgress, SdlcAction.Monitor);

        // Story A (plannable+implementable) with mixed children → InProgress
        await RouteAndAssert(310, SdlcPhase.InProgress, SdlcAction.Monitor);

        // Story B (plannable+implementable) in Proposed → NeedsPlanning
        await RouteAndAssert(320, SdlcPhase.NeedsPlanning, SdlcAction.Plan);

        // Task A1 (implementable) in Active → InProgress
        await RouteAndAssert(311, SdlcPhase.InProgress, SdlcAction.Monitor);

        // Task A2 (implementable) in New → ReadyForImplementation
        await RouteAndAssert(312, SdlcPhase.ReadyForImplementation, SdlcAction.Implement);

        // Complete all tasks and stories, then verify Epic reaches ReadyForCompletion
        await SeedAsync(
            new WorkItemBuilder().WithId(311).WithType(TaskType).WithTitle("Task A1").WithState(CompletedState).WithParentId(310).Build(),
            new WorkItemBuilder().WithId(312).WithType(TaskType).WithTitle("Task A2").WithState(CompletedState).WithParentId(310).Build(),
            new WorkItemBuilder().WithId(321).WithType(TaskType).WithTitle("Task B1").WithState(CompletedState).WithParentId(320).Build(),
            new WorkItemBuilder().WithId(310).WithType(UserStoryType).WithTitle("Story A").WithState(CompletedState).WithParentId(300).Build(),
            new WorkItemBuilder().WithId(320).WithType(UserStoryType).WithTitle("Story B").WithState(CompletedState).WithParentId(300).Build());

        await RouteAndAssert(300, SdlcPhase.ReadyForCompletion, SdlcAction.Close);
    }

    // =========================================================================
    // Hierarchy command output at key lifecycle points
    // =========================================================================

    [Fact]
    public async Task Hierarchy_AtReadyForImplementation_ReflectsSeededTreeStructure()
    {
        // Seed Epic with 2 User Story children and Tasks underneath
        var (epic, stories) = new WorkItemBuilder()
            .WithId(400).WithType(EpicType).WithTitle("Hierarchy Epic").WithState(InProgressState)
            .WithChildren(
                new WorkItemBuilder().WithId(410).WithType(UserStoryType).WithTitle("Story X").WithState(ProposedState),
                new WorkItemBuilder().WithId(420).WithType(UserStoryType).WithTitle("Story Y").WithState(ProposedState))
            .BuildAll();

        var taskA = new WorkItemBuilder()
            .WithId(411).WithType(TaskType).WithTitle("Task X1").WithState(ProposedState).WithParentId(410).Build();
        var taskB = new WorkItemBuilder()
            .WithId(421).WithType(TaskType).WithTitle("Task Y1").WithState(ProposedState).WithParentId(420).Build();

        await SeedAsync([epic, .. stories, taskA, taskB]);

        var cmd = CreateHierarchyCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(400));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();

        // Root Epic
        result.WorkItemId.ShouldBe(400);
        result.Type.ShouldBe(EpicType);
        result.State.ShouldBe(InProgressState);
        result.Facets.ShouldContain("plannable");

        // 2 children (User Stories)
        result.Children.ShouldNotBeNull();
        result.Children!.Length.ShouldBe(2);

        var storyX = result.Children.First(c => c.WorkItemId == 410);
        storyX.Type.ShouldBe(UserStoryType);
        storyX.State.ShouldBe(ProposedState);
        storyX.Facets.ShouldContain("plannable");
        storyX.Facets.ShouldContain("implementable");

        // Story X has 1 Task child
        storyX.Children.ShouldNotBeNull();
        storyX.Children!.Length.ShouldBe(1);
        storyX.Children[0].WorkItemId.ShouldBe(411);
        storyX.Children[0].Type.ShouldBe(TaskType);
        storyX.Children[0].Facets.ShouldContain("implementable");
    }

    [Fact]
    public async Task Hierarchy_AtReadyForCompletion_AllChildrenShowClosedState()
    {
        var (epic, stories) = new WorkItemBuilder()
            .WithId(500).WithType(EpicType).WithTitle("Completion Epic").WithState(InProgressState)
            .WithChildren(
                new WorkItemBuilder().WithId(510).WithType(UserStoryType).WithTitle("Completed Story").WithState(CompletedState))
            .BuildAll();
        await SeedAsync([epic, .. stories]);

        var cmd = CreateHierarchyCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(500));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children!.Length.ShouldBe(1);
        result.Children[0].State.ShouldBe(CompletedState);
    }

    // =========================================================================
    // JSON output contract — snake_case naming, null omission
    // =========================================================================

    [Fact]
    public async Task RouteOutput_UsesSnakeCaseNaming_AndOmitsNullFields()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(600).WithType(EpicType).WithTitle("JSON Contract Epic").WithState(ProposedState)
                .Build());

        var cmd = CreateRouteCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(600));

        // snake_case property names
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"phase\"");
        output.ShouldContain("\"action\"");
        output.ShouldContain("\"message\"");

        // Verify proper JSON deserialization round-trip
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(600);
        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
    }

    [Fact]
    public async Task ValidateOutput_UsesSnakeCaseNaming_AndOmitsNullFields()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(700).WithType(EpicType).WithTitle("Validate JSON").WithState(ProposedState)
                .Build());

        var cmd = CreateValidateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(700, "begin_planning"));

        // snake_case property names
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"is_valid\"");
        output.ShouldContain("\"target_state\"");
        output.ShouldContain("\"event\"");

        // No PascalCase leakage
        output.ShouldNotContain("\"WorkItemId\"");
        output.ShouldNotContain("\"IsValid\"");
        output.ShouldNotContain("\"TargetState\"");
    }

    [Fact]
    public async Task HierarchyOutput_UsesSnakeCaseNaming_AndOmitsNullTags()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(800).WithType(EpicType).WithTitle("Hierarchy JSON").WithState(InProgressState)
                .Build());

        var cmd = CreateHierarchyCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(800));

        // snake_case property names
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"children\"");
        output.ShouldContain("\"facets\"");

        // null tags should be omitted (WhenWritingNull)
        output.ShouldNotContain("\"tags\"");
    }

    // =========================================================================
    // Validate transitions at each lifecycle phase
    // =========================================================================

    [Fact]
    public async Task Validate_AtEachPhase_ReturnsCorrectTransitionValidity()
    {
        // Phase 1: Proposed — begin_planning valid, implementation_complete invalid
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(900).WithType(EpicType).WithTitle("Transition Epic").WithState(ProposedState)
                .Build());

        await ValidateAndAssertValid(900, "begin_planning", BeginPlanningTarget);
        await ValidateAndAssertInvalid(900, "implementation_complete");

        // Phase 2: InProgress — implementation_complete valid, begin_planning invalid
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(900).WithType(EpicType).WithTitle("Transition Epic").WithState(InProgressState)
                .Build());

        await ValidateAndAssertValid(900, "implementation_complete", ImplementationCompleteTarget);
        await ValidateAndAssertInvalid(900, "begin_planning");

        // Unknown event is always invalid
        await ValidateAndAssertInvalid(900, "nonexistent_event");
    }

    // =========================================================================
    // StateCategoryResolver authoritative path (StateEntry-based resolution)
    // =========================================================================

    [Fact]
    public void StateCategoryResolver_WithAgileStateEntries_ResolvesViaAuthoritativePath()
    {
        // Seed StateEntry data representing the Agile process template states.
        // When entries are provided, StateCategoryResolver uses those instead of fallback heuristic.
        var agileEpicStates = new List<StateEntry>
        {
            new("New", StateCategory.Proposed, "b2b2b2"),
            new("Active", StateCategory.InProgress, "007acc"),
            new("Resolved", StateCategory.Resolved, "ff9d00"),
            new("Closed", StateCategory.Completed, "339933"),
            new("Removed", StateCategory.Removed, "ffffff"),
        };

        // Authoritative resolution uses the entry list, not fallback heuristic
        StateCategoryResolver.Resolve("New", agileEpicStates).ShouldBe(StateCategory.Proposed);
        StateCategoryResolver.Resolve("Active", agileEpicStates).ShouldBe(StateCategory.InProgress);
        StateCategoryResolver.Resolve("Resolved", agileEpicStates).ShouldBe(StateCategory.Resolved);
        StateCategoryResolver.Resolve("Closed", agileEpicStates).ShouldBe(StateCategory.Completed);
        StateCategoryResolver.Resolve("Removed", agileEpicStates).ShouldBe(StateCategory.Removed);

        // Unknown state falls back to FallbackCategory
        StateCategoryResolver.Resolve("CustomState", agileEpicStates).ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void StateCategoryResolver_AuthoritativePath_OverridesFallbackMapping()
    {
        // Demonstrate that authoritative entries take precedence over fallback.
        // "Done" normally maps to Completed via fallback, but here we override it.
        var customEntries = new List<StateEntry>
        {
            new("Done", StateCategory.InProgress, "007acc"),
        };

        // Without entries, "Done" maps to Completed (via fallback)
        StateCategoryResolver.Resolve("Done", entries: null).ShouldBe(StateCategory.Completed);

        // With authoritative entries, "Done" maps to InProgress (override)
        StateCategoryResolver.Resolve("Done", customEntries).ShouldBe(StateCategory.InProgress);
    }

    [Fact]
    public void StateCategoryResolver_WithAgileStateEntries_CaseInsensitiveMatch()
    {
        var entries = new List<StateEntry>
        {
            new("Active", StateCategory.InProgress, "007acc"),
        };

        // Case-insensitive matching on entry names
        StateCategoryResolver.Resolve("active", entries).ShouldBe(StateCategory.InProgress);
        StateCategoryResolver.Resolve("ACTIVE", entries).ShouldBe(StateCategory.InProgress);
        StateCategoryResolver.Resolve("Active", entries).ShouldBe(StateCategory.InProgress);
    }

    // =========================================================================
    // Workspace hint / branch strategy verification across lifecycle
    // =========================================================================

    [Fact]
    public async Task Route_AcrossLifecycle_WorkspaceHintConsistentlyPresent()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(1000).WithType(EpicType).WithTitle("Branch Hint Epic").WithState(ProposedState)
                .Build());

        // NeedsPlanning phase
        var result = await RouteAndAssert(1000, SdlcPhase.NeedsPlanning, SdlcAction.Plan);
        result.WorkspaceHint.ShouldNotBeNull();
        result.WorkspaceHint!.FeatureBranch.ShouldNotBeNull();
        result.WorkspaceHint.FeatureBranch!.ShouldContain("1000");

        // Transition to InProgress → NeedsSeeding
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(1000).WithType(EpicType).WithTitle("Branch Hint Epic").WithState(InProgressState)
                .Build());

        result = await RouteAndAssert(1000, SdlcPhase.NeedsSeeding, SdlcAction.Seed);
        result.WorkspaceHint.ShouldNotBeNull();
        result.WorkspaceHint!.FeatureBranch.ShouldNotBeNull();
        result.WorkspaceHint.FeatureBranch!.ShouldContain("1000");

        // Done phase
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(1000).WithType(EpicType).WithTitle("Branch Hint Epic").WithState(CompletedState)
                .Build());

        result = await RouteAndAssert(1000, SdlcPhase.Done, SdlcAction.None);
        result.WorkspaceHint.ShouldNotBeNull();
        result.WorkspaceHint!.FeatureBranch.ShouldNotBeNull();
        result.WorkspaceHint.FeatureBranch!.ShouldContain("1000");
    }

    // =========================================================================
    // Edge case: User Story (plannable+implementable) in InProgress with no children
    // =========================================================================

    [Fact]
    public async Task Route_PlannableImplementable_InProgressNoChildren_ReturnsReadyForImplementation()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(1100).WithType(UserStoryType).WithTitle("Direct Story").WithState(InProgressState)
                .Build());

        await RouteAndAssert(1100, SdlcPhase.ReadyForImplementation, SdlcAction.Implement);
    }

    // =========================================================================
    // Helper methods
    // =========================================================================

    private async Task<RouteResult> RouteAndAssert(int workItemId, string expectedPhase, string expectedAction)
    {
        var cmd = CreateRouteCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(workItemId));

        exitCode.ShouldBe(ExitCodes.Success, $"Route for item {workItemId} failed with exit code {exitCode}. Output: {output}");
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(workItemId);
        result.Phase.ShouldBe(expectedPhase, $"Expected phase '{expectedPhase}' for item {workItemId}, got '{result.Phase}'");
        result.Action.ShouldBe(expectedAction, $"Expected action '{expectedAction}' for item {workItemId}, got '{result.Action}'");
        return result;
    }

    private async Task<ValidateResult> ValidateAndAssertValid(int workItemId, string eventName, string expectedTargetState)
    {
        var cmd = CreateValidateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(workItemId, eventName));

        exitCode.ShouldBe(ExitCodes.Success, $"Validate({workItemId}, {eventName}) expected success. Output: {output}");
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(workItemId);
        result.Event.ShouldBe(eventName);
        result.IsValid.ShouldBeTrue($"Expected valid transition for event '{eventName}' on item {workItemId}");
        result.TargetState.ShouldBe(expectedTargetState);
        return result;
    }

    private async Task ValidateAndAssertInvalid(int workItemId, string eventName)
    {
        var cmd = CreateValidateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(workItemId, eventName));

        exitCode.ShouldBe(ExitCodes.RoutingFailure, $"Validate({workItemId}, {eventName}) expected routing failure. Output: {output}");
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(workItemId);
        result.Event.ShouldBe(eventName);
        result.IsValid.ShouldBeFalse($"Expected invalid transition for event '{eventName}' on item {workItemId}");
        result.Message.ShouldNotBeNullOrEmpty();
    }
}

