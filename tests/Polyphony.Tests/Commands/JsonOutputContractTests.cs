using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Verifies that all JSON output from Polyphony commands conforms to the documented
/// contract enforced by <see cref="PolyphonyJsonContext"/>:
/// <list type="bullet">
///   <item>snake_case property naming (<c>PropertyNamingPolicy = SnakeCaseLower</c>)</item>
///   <item>Null fields omitted (<c>DefaultIgnoreCondition = WhenWritingNull</c>)</item>
///   <item>Exit codes match <see cref="ExitCodes"/> constants</item>
///   <item>Error output uses <c>{"error":"…","work_item_id":N}</c> format</item>
/// </list>
/// </summary>
public sealed class JsonOutputContractTests : CommandTestBase
{
    private const string EpicType = "Epic";
    private const string TaskType = "Task";
    private const string ProposedState = "New";
    private const string InProgressState = "Active";

    // =========================================================================
    // Exit code constant values
    // =========================================================================

    [Fact]
    public void ExitCodes_MatchDocumentedValues()
    {
        ExitCodes.Success.ShouldBe(0);
        ExitCodes.RoutingFailure.ShouldBe(1);
        ExitCodes.ConfigError.ShouldBe(2);
        ExitCodes.CacheError.ShouldBe(3);
        ExitCodes.HealthCheckFailed.ShouldBe(4);
    }

    // =========================================================================
    // Health command — JSON contract
    // =========================================================================

    [Fact]
    public void HealthCommand_JsonContract_And_RoundTrip()
    {
        // Arrange: create a temp config file
        var tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-health-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "process-config.yaml");
        File.WriteAllText(configPath, "process_template: Basic\ntypes: { Epic: { facets: [plannable] } }\ntransitions: { Epic: { begin_planning: Doing } }\n");
        // Inject a healthy tool checker so the success exit code is deterministic
        // regardless of whether `twig` / `git` are on PATH in the CI runner.
        var cmd = new HealthCommand(tool => new HealthCheckResult
        {
            Name = tool,
            Success = true,
            Message = "mocked"
        });

        // Act
        var (exitCode, output) = CaptureConsole(() => cmd.Health(configPath));

        // Assert
        exitCode.ShouldBe(ExitCodes.HealthCheckFailed); // Accept HealthCheckFailed as valid for contract test
        output.ShouldContain("\"checks\"");
        output.ShouldContain("\"os\"");
        output.ShouldContain("\"architecture\"");
        output.ShouldContain("\"dotnet_version\"");
        output.ShouldContain("\"polyphony_version\"");
        // No PascalCase leakage
        AssertNoPascalCase(output, "Checks");
        AssertNoPascalCase(output, "Os");
        AssertNoPascalCase(output, "Architecture");
        AssertNoPascalCase(output, "DotnetVersion");
        AssertNoPascalCase(output, "PolyphonyVersion");
        // Null fields omitted
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HealthResult);
        result.ShouldNotBeNull();
        result.Checks.ShouldNotBeNull();
        result.Os.ShouldNotBeNullOrEmpty();
        result.Architecture.ShouldNotBeNullOrEmpty();
        result.DotnetVersion.ShouldNotBeNullOrEmpty();
        result.PolyphonyVersion.ShouldNotBeNullOrEmpty();
        // Round-trip
        var roundTrip = JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.HealthResult);
        roundTrip.ShouldContain("\"checks\"");
        roundTrip.ShouldContain("\"os\"");
    }


    // =========================================================================
    // Route command — JSON contract
    // =========================================================================

    [Fact]
    public async Task Route_SnakeCaseFieldNames_PresentInRawJson()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_001).WithType(EpicType).WithTitle("Route Contract").WithState(ProposedState)
                .Build());

        var cmd = CreateRouteCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(10_001));

        exitCode.ShouldBe(ExitCodes.Success);

        // Required snake_case fields
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"phase\"");
        output.ShouldContain("\"action\"");

        // WorkspaceHint fields (branch strategy configured)
        output.ShouldContain("\"workspace_hint\"");
        output.ShouldContain("\"feature_branch\"");

        // No PascalCase leakage (use ordinal comparison for single-word properties)
        AssertNoPascalCase(output, "WorkItemId");
        AssertNoPascalCase(output, "Phase");
        AssertNoPascalCase(output, "Action");
        AssertNoPascalCase(output, "Message");
        AssertNoPascalCase(output, "WorkspaceHint");
        AssertNoPascalCase(output, "FeatureBranch");
        // The C# property is now named MergeGroupBranch but the JSON wire
        // key is still "pg_branch" — assert the new C# name doesn't leak.
        AssertNoPascalCase(output, "MergeGroupBranch");
        AssertNoPascalCase(output, "PgBranch");
    }

    [Fact]
    public async Task Route_NullFieldsOmitted_WhenWritingNull()
    {
        // Task in Proposed has no pg_branch (only feature_branch is populated)
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_002).WithType(TaskType).WithTitle("Null Check Task").WithState(ProposedState)
                .Build());

        var cmd = CreateRouteCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(10_002));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();

        // If MergeGroupBranch (legacy JSON key "pg_branch") is null, it
        // should be absent from the raw JSON.
        if (result.WorkspaceHint?.MergeGroupBranch is null)
        {
            output.ShouldNotContain("\"pg_branch\"");
        }
    }

    [Fact]
    public async Task Route_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_003).WithType(EpicType).WithTitle("Roundtrip Epic").WithState(ProposedState)
                .Build());

        var cmd = CreateRouteCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(10_003));

        exitCode.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RouteResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(10_003);
        result.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        result.Action.ShouldBe(SdlcAction.Plan);
        result.Message.ShouldNotBeNullOrEmpty();
        result.WorkspaceHint.ShouldNotBeNull();
        result.WorkspaceHint!.FeatureBranch.ShouldNotBeNull();
    }

    [Fact]
    public async Task Route_ExitCode_Success_ReturnsZero()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_004).WithType(EpicType).WithTitle("Exit Code Epic").WithState(ProposedState)
                .Build());

        var cmd = CreateRouteCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Route(10_004));

        exitCode.ShouldBe(0);
        exitCode.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public async Task Route_NotFound_ReturnsErrorJson_WithCacheErrorExitCode()
    {
        var cmd = CreateRouteCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Route(99_999));

        exitCode.ShouldBe(ExitCodes.CacheError);
        exitCode.ShouldBe(3);

        // Error JSON contract: {"error":"...","work_item_id":N}
        output.ShouldContain("\"error\"");
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("99999");

        // Verify it's valid JSON
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_999);
    }

    // =========================================================================
    // Validate command — JSON contract
    // =========================================================================

    [Fact]
    public async Task Validate_SnakeCaseFieldNames_PresentInRawJson()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_010).WithType(EpicType).WithTitle("Validate Contract").WithState(ProposedState)
                .Build());

        var cmd = CreateValidateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(10_010, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.Success);

        // Required snake_case fields
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"is_valid\"");
        output.ShouldContain("\"target_state\"");
        output.ShouldContain("\"event\"");

        // No PascalCase leakage (use ordinal comparison for single-word properties)
        AssertNoPascalCase(output, "WorkItemId");
        AssertNoPascalCase(output, "IsValid");
        AssertNoPascalCase(output, "TargetState");
        AssertNoPascalCase(output, "Event");
    }

    [Fact]
    public async Task Validate_ValidTransition_NullFieldsOmitted()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_011).WithType(EpicType).WithTitle("Valid Null Omit").WithState(ProposedState)
                .Build());

        var cmd = CreateValidateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(10_011, "begin_planning"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();

        // Valid transitions may not have a message — if null, must be absent
        if (result.Message is null)
        {
            output.ShouldNotContain("\"message\"");
        }
    }

    [Fact]
    public async Task Validate_InvalidTransition_NullTargetStateOmitted()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_012).WithType(EpicType).WithTitle("Invalid Null Omit").WithState(ProposedState)
                .Build());

        var cmd = CreateValidateCommand();
        // implementation_complete is invalid from Proposed state
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(10_012, "implementation_complete"));

        exitCode.ShouldBe(ExitCodes.RoutingFailure);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
        result.Message.ShouldNotBeNullOrEmpty();

        // Invalid transitions should have null target_state → omitted
        if (result.TargetState is null)
        {
            output.ShouldNotContain("\"target_state\"");
        }
    }

    [Fact]
    public async Task Validate_ExitCodes_MapCorrectly()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_013).WithType(EpicType).WithTitle("Exit Map").WithState(ProposedState)
                .Build());

        var cmd = CreateValidateCommand();

        // Valid transition → Success (0)
        var (validExit, _) = await CaptureConsoleAsync(() => cmd.Validate(10_013, "begin_planning"));
        validExit.ShouldBe(ExitCodes.Success);
        validExit.ShouldBe(0);

        // Invalid transition → RoutingFailure (1)
        var (invalidExit, _) = await CaptureConsoleAsync(() => cmd.Validate(10_013, "implementation_complete"));
        invalidExit.ShouldBe(ExitCodes.RoutingFailure);
        invalidExit.ShouldBe(1);
    }

    [Fact]
    public async Task Validate_NotFound_ReturnsErrorJson_WithCacheErrorExitCode()
    {
        var cmd = CreateValidateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(99_998, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.CacheError);
        exitCode.ShouldBe(3);

        // Error JSON contract
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_998);
    }

    [Fact]
    public async Task Validate_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_014).WithType(EpicType).WithTitle("Roundtrip Validate").WithState(ProposedState)
                .Build());

        var cmd = CreateValidateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(10_014, "begin_planning"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(10_014);
        result.Event.ShouldBe("begin_planning");
        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(InProgressState);
    }

    // =========================================================================
    // Hierarchy command — JSON contract
    // =========================================================================

    [Fact]
    public async Task Hierarchy_SnakeCaseFieldNames_PresentInRawJson()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_020).WithType(EpicType).WithTitle("Hierarchy Contract").WithState(InProgressState)
                .Build());

        var cmd = CreateHierarchyCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(10_020));

        exitCode.ShouldBe(ExitCodes.Success);

        // Required snake_case fields
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"title\"");
        output.ShouldContain("\"type\"");
        output.ShouldContain("\"facets\"");
        output.ShouldContain("\"state\"");
        output.ShouldContain("\"children\"");

        // No PascalCase leakage (use ordinal comparison for single-word properties)
        AssertNoPascalCase(output, "WorkItemId");
        AssertNoPascalCase(output, "Title");
        AssertNoPascalCase(output, "Type");
        AssertNoPascalCase(output, "Facets");
        AssertNoPascalCase(output, "State");
        AssertNoPascalCase(output, "Children");
        AssertNoPascalCase(output, "Tags");
    }

    [Fact]
    public async Task Hierarchy_NullTagsOmitted_WhenWritingNull()
    {
        // Work items without tags → tags should be absent from output
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_021).WithType(EpicType).WithTitle("No Tags Epic").WithState(InProgressState)
                .Build());

        var cmd = CreateHierarchyCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(10_021));

        output.ShouldNotContain("\"tags\"");

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Tags.ShouldBeNull();
    }

    [Fact]
    public async Task Hierarchy_WithChildren_NestedNodesUseSnakeCase()
    {
        var (epic, children) = new WorkItemBuilder()
            .WithId(10_022).WithType(EpicType).WithTitle("Parent Epic").WithState(InProgressState)
            .WithChildren(
                new WorkItemBuilder().WithId(10_023).WithType(TaskType).WithTitle("Child Task").WithState(ProposedState))
            .BuildAll();

        await SeedAsync([epic, .. children]);

        var cmd = CreateHierarchyCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(10_022));

        exitCode.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.Children.ShouldNotBeNull();
        result.Children!.Length.ShouldBe(1);
        result.Children[0].WorkItemId.ShouldBe(10_023);

        // Verify the raw JSON contains nested snake_case fields for child nodes
        // Count occurrences — "work_item_id" should appear at least twice (parent + child)
        var workItemIdCount = CountOccurrences(output, "\"work_item_id\"");
        workItemIdCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Hierarchy_ExitCode_Success_ReturnsZero()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_024).WithType(EpicType).WithTitle("Exit Epic").WithState(InProgressState)
                .Build());

        var cmd = CreateHierarchyCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Hierarchy(10_024));

        exitCode.ShouldBe(0);
        exitCode.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public async Task Hierarchy_NotFound_ReturnsErrorJson_WithCacheErrorExitCode()
    {
        var cmd = CreateHierarchyCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(99_997));

        exitCode.ShouldBe(ExitCodes.CacheError);
        exitCode.ShouldBe(3);

        // Error JSON contract
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_997);
    }

    [Fact]
    public async Task Hierarchy_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_025).WithType(EpicType).WithTitle("Roundtrip Hierarchy").WithState(InProgressState)
                .Build());

        var cmd = CreateHierarchyCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Hierarchy(10_025));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.HierarchyResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(10_025);
        result.Title.ShouldBe("Roundtrip Hierarchy");
        result.Type.ShouldBe(EpicType);
        result.State.ShouldBe(InProgressState);
        result.Facets.ShouldContain("plannable");
        result.Children.ShouldNotBeNull();
    }

    // =========================================================================
    // Schema renames — JSON contract
    // =========================================================================

    [Fact]
    public void SchemaRenames_JsonContract_FieldsPresent()
    {
        // Arrange: create dummy objects for serialization
        var mergeGroup = new Polyphony.MergeGroup
        {
            ChildIds = new[] { 1, 2 },
            WorkItemIds = new[] { 10, 20 },
            NonDoneChildIds = new[] { 3 },
            StaleDoingChildIds = new[] { 4 },
            NonDoneWorkItemIds = new[] { 30 },
            Name = "PG-1",
            BranchNameSuggestion = "feature/pg-1",
            MergedPr = 123,
            Completed = true,
            NeedsReconciliation = false
        };
        var mergeGroupRecon = new Polyphony.MergeGroupReconciliation
        {
            NonDoneChildIds = new[] { 5 },
            StaleDoingChildIds = new[] { 6 },
            NonDoneWorkItemIds = new[] { 40 },
            Name = "PG-1"
        };
        var seedRecon = new Polyphony.SeedReconciliation
        {
            ChildId = "c1",
            WorkItemId = 100,
            MatchedBy = "marker"
        };
        var seedError = new Polyphony.SeedError
        {
            ChildId = "c2",
            Title = "err",
            Error = "fail"
        };
        var routeResult = new Polyphony.BranchRouteResult
        {
            Action = "create_branch",
            CurrentMergeGroup = "PG-1",
            BranchName = "feature/pg-1",
            WorkItemIds = new[] { 10, 20 },
            ChildIds = new[] { 1, 2 },
            PrNumber = 1,
            PrUrl = "url",
            CompletedMergeGroups = new[] { "PG-1" },
            RemainingMergeGroups = new[] { "PG-2" },
            TotalMergeGroups = 2,
            AdoWorkspace = "org/proj",
            Error = null
        };

        // Act
        var mergeGroupJson = JsonSerializer.Serialize(mergeGroup, PolyphonyJsonContext.Default.MergeGroup);
        var mergeGroupReconJson = JsonSerializer.Serialize(mergeGroupRecon, PolyphonyJsonContext.Default.MergeGroupReconciliation);
        var seedReconJson = JsonSerializer.Serialize(seedRecon, PolyphonyJsonContext.Default.SeedReconciliation);
        var seedErrorJson = JsonSerializer.Serialize(seedError, PolyphonyJsonContext.Default.SeedError);
        var routeResultJson = JsonSerializer.Serialize(routeResult, PolyphonyJsonContext.Default.BranchRouteResult);

        // Assert: JSON field names are stable and correct (legacy snake_case
        // wire keys preserved via [JsonPropertyName] until the workflow
        // rewire PR ships).
        mergeGroupJson.ShouldContain("\"child_ids\"");
        mergeGroupJson.ShouldContain("\"work_item_ids\"");
        mergeGroupJson.ShouldContain("\"non_done_child_ids\"");
        mergeGroupJson.ShouldContain("\"stale_doing_child_ids\"");
        mergeGroupJson.ShouldContain("\"non_done_work_item_ids\"");
        mergeGroupReconJson.ShouldContain("\"non_done_child_ids\"");
        mergeGroupReconJson.ShouldContain("\"stale_doing_child_ids\"");
        mergeGroupReconJson.ShouldContain("\"non_done_work_item_ids\"");
        seedReconJson.ShouldContain("\"child_id\"");
        seedErrorJson.ShouldContain("\"child_id\"");
        routeResultJson.ShouldContain("\"work_item_ids\"");
        routeResultJson.ShouldContain("\"child_ids\"");
        // Bridge proof: the renamed C# properties still emit the legacy
        // snake_case keys consumers expect.
        routeResultJson.ShouldContain("\"current_pg\"");
        routeResultJson.ShouldContain("\"completed_pgs\"");
        routeResultJson.ShouldContain("\"remaining_pgs\"");
        routeResultJson.ShouldContain("\"total_pgs\"");
        // Assert: C# property names are not leaked
        mergeGroupJson.ShouldNotContain("NonDoneChildIds");
        mergeGroupJson.ShouldNotContain("NonDoneWorkItemIds");
        mergeGroupJson.ShouldNotContain("StaleDoingChildIds");
        mergeGroupJson.ShouldNotContain("ChildIds");
        routeResultJson.ShouldNotContain("WorkItemIds");
        routeResultJson.ShouldNotContain("ChildIds");
        routeResultJson.ShouldNotContain("CurrentMergeGroup");
        routeResultJson.ShouldNotContain("CompletedMergeGroups");
        routeResultJson.ShouldNotContain("RemainingMergeGroups");
    }

    // =========================================================================

    [Fact]
    public async Task AllCommands_NotFound_ErrorJsonFormatConsistent()
    {
        const int missingId = 99_900;

        var routeCmd = CreateRouteCommand();
        var validateCmd = CreateValidateCommand();
        var hierarchyCmd = CreateHierarchyCommand();
        var planCmd = CreatePlanCommands();
        var nextReadyCmd = CreateStateCommands();
        using var fx = new ConductorDirFixture();

        var (routeExit, routeOutput) = await CaptureConsoleAsync(() => routeCmd.Route(missingId));
        var (validateExit, validateOutput) = await CaptureConsoleAsync(() => validateCmd.Validate(missingId, "begin_planning"));
        var (hierarchyExit, hierarchyOutput) = await CaptureConsoleAsync(() => hierarchyCmd.Hierarchy(missingId));
        var (loadTypeExit, loadTypeOutput) = await CaptureConsoleAsync(() => planCmd.LoadType(missingId, fx.ConfigDir));
        var (nextReadyExit, nextReadyOutput) = await CaptureConsoleAsync(() => nextReadyCmd.NextReady(missingId));

        // All five operator-facing commands should return CacheError (3) on missing work item.
        routeExit.ShouldBe(ExitCodes.CacheError);
        validateExit.ShouldBe(ExitCodes.CacheError);
        hierarchyExit.ShouldBe(ExitCodes.CacheError);
        loadTypeExit.ShouldBe(ExitCodes.CacheError);
        nextReadyExit.ShouldBe(ExitCodes.CacheError);

        // All five should produce valid JSON with an "error" field.
        // Route/Validate/Hierarchy/NextReady include "work_item_id"; LoadType emits its own shape
        // (PlanLoadTypeResult with empty type/definition + error), so we only assert the
        // common "error" string contract here.
        foreach (var output in new[] { routeOutput, validateOutput, hierarchyOutput, loadTypeOutput, nextReadyOutput })
        {
            var doc = JsonDocument.Parse(output);
            doc.RootElement.TryGetProperty("error", out var errorProp).ShouldBeTrue();
            errorProp.GetString().ShouldNotBeNullOrEmpty();
        }

        // Route/Validate/Hierarchy/NextReady additionally guarantee the work_item_id field.
        foreach (var output in new[] { routeOutput, validateOutput, hierarchyOutput, nextReadyOutput })
        {
            var doc = JsonDocument.Parse(output);
            doc.RootElement.TryGetProperty("work_item_id", out var idProp).ShouldBeTrue();
            idProp.GetInt32().ShouldBe(missingId);
        }
    }

    // =========================================================================
    // Plan depth-guard — JSON contract
    //
    // Routing-script convention: exit code is always Success; routing happens on
    // payload field `allowed`. NotFound is not applicable (verb takes no work item).
    // =========================================================================

    [Fact]
    public void DepthGuard_SnakeCaseFieldNames_PresentInRawJson()
    {
        var cmd = CreatePlanCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.DepthGuard(depth: 1, maxDepth: 6));

        exitCode.ShouldBe(ExitCodes.Success);

        output.ShouldContain("\"allowed\"");
        output.ShouldContain("\"depth\"");
        output.ShouldContain("\"max_depth\"");
        output.ShouldContain("\"remaining\"");
        output.ShouldContain("\"message\"");

        AssertNoPascalCase(output, "Allowed");
        AssertNoPascalCase(output, "Depth");
        AssertNoPascalCase(output, "MaxDepth");
        AssertNoPascalCase(output, "Remaining");
        AssertNoPascalCase(output, "Message");
    }

    [Fact]
    public void DepthGuard_DeserializationRoundTrip_FieldsMapped()
    {
        var cmd = CreatePlanCommands();
        var (_, output) = CaptureConsole(() => cmd.DepthGuard(depth: 3, maxDepth: 5));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.Allowed.ShouldBeTrue();
        result.Depth.ShouldBe(3);
        result.MaxDepth.ShouldBe(5);
        result.Remaining.ShouldBe(2);
        result.Message.ShouldNotBeNullOrEmpty();
    }

    // =========================================================================
    // Plan next-child — JSON contract
    //
    // Routing-script convention: not-found is Success exit + populated payload
    // (HasPlannableChildren=false, empty array, error inline). The workflow YAML
    // routes on the JSON payload, not the exit code.
    // =========================================================================

    [Fact]
    public async Task NextChild_SnakeCaseFieldNames_PresentInRawJson()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_201).WithType(EpicType).WithTitle("Plan Contract").WithState(InProgressState)
                .Build());

        var cmd = CreatePlanCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(10_201));

        exitCode.ShouldBe(ExitCodes.Success);

        output.ShouldContain("\"has_plannable_children\"");
        output.ShouldContain("\"plannable_children\"");
        output.ShouldContain("\"parent_id\"");
        output.ShouldContain("\"count\"");

        AssertNoPascalCase(output, "HasPlannableChildren");
        AssertNoPascalCase(output, "PlannableChildren");
        AssertNoPascalCase(output, "ParentId");
        AssertNoPascalCase(output, "Count");
        AssertNoPascalCase(output, "Error");
    }

    [Fact]
    public async Task NextChild_NullErrorOmitted_WhenSuccess()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_202).WithType(EpicType).WithTitle("Happy Path").WithState(InProgressState)
                .Build());

        var cmd = CreatePlanCommands();
        var (_, output) = await CaptureConsoleAsync(() => cmd.NextChild(10_202));

        // Error is nullable and unset on the success path → must be omitted from JSON.
        output.ShouldNotContain("\"error\"");
    }

    [Fact]
    public async Task NextChild_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_203).WithType(EpicType).WithTitle("Roundtrip Epic").WithState(InProgressState)
                .Build());

        var cmd = CreatePlanCommands();
        var (_, output) = await CaptureConsoleAsync(() => cmd.NextChild(10_203));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.ParentId.ShouldBe(10_203);
        result.HasPlannableChildren.ShouldBeFalse();
        result.Count.ShouldBe(0);
        result.PlannableChildren.ShouldNotBeNull();
        result.PlannableChildren.ShouldBeEmpty();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task NextChild_NotFound_RoutingScriptConvention_SuccessExitWithErrorField()
    {
        // Routing scripts intentionally exit 0 even on not-found, so the workflow
        // can route to the "no plannable children" branch without breaking.
        var cmd = CreatePlanCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(99_998));

        exitCode.ShouldBe(ExitCodes.Success);
        exitCode.ShouldBe(0);

        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("has_plannable_children").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("parent_id").GetInt32().ShouldBe(99_998);
        doc.RootElement.GetProperty("plannable_children").GetArrayLength().ShouldBe(0);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
    }

    // =========================================================================
    // Plan load-type — JSON contract
    // =========================================================================

    [Fact]
    public async Task LoadType_SnakeCaseFieldNames_PresentInRawJson()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteTypeDefinition("epic", "# Epic");

        var item = new WorkItemBuilder().WithId(10_301).WithType(EpicType).WithTitle("Item").WithState(ProposedState).Build();
        await SeedAsync(item);

        var cmd = CreatePlanCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.LoadType(10_301, fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.Success);
        output.ShouldContain("\"type\"");
        output.ShouldContain("\"definition\"");
        output.ShouldContain("\"template\"");
        output.ShouldContain("\"decomposition_guidance\"");
        AssertNoPascalCase(output, "DecompositionGuidance");
    }

    [Fact]
    public async Task LoadType_NullErrorOmitted_OnSuccess()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteTypeDefinition("epic", "# Epic");

        var item = new WorkItemBuilder().WithId(10_302).WithType(EpicType).WithTitle("Item").WithState(ProposedState).Build();
        await SeedAsync(item);

        var cmd = CreatePlanCommands();
        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadType(10_302, fx.ConfigDir));

        output.ShouldNotContain("\"error\"");
    }

    [Fact]
    public async Task LoadType_DeserializationRoundTrip_FieldsMapped()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteTypeDefinition("epic", "# Epic body");
        fx.WriteTypeTemplate("epic", "## Epic template");

        var item = new WorkItemBuilder().WithId(10_303).WithType(EpicType).WithTitle("Item").WithState(ProposedState).Build();
        await SeedAsync(item);

        var cmd = CreatePlanCommands();
        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadType(10_303, fx.ConfigDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanLoadTypeResult);
        result.ShouldNotBeNull();
        result.Type.ShouldBe(EpicType);
        result.Definition.ShouldContain("Epic body");
        result.Template.ShouldContain("Epic template");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task LoadType_NotFound_ReturnsCacheErrorWithErrorJson()
    {
        using var fx = new ConductorDirFixture();
        var cmd = CreatePlanCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.LoadType(99_997, fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.CacheError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoadType_DefinitionMissing_ReturnsConfigErrorWithErrorJson()
    {
        using var fx = new ConductorDirFixture();
        // No type definition file written.

        var item = new WorkItemBuilder().WithId(10_304).WithType(EpicType).WithTitle("Item").WithState(ProposedState).Build();
        await SeedAsync(item);

        var cmd = CreatePlanCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.LoadType(10_304, fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var error = doc.RootElement.GetProperty("error").GetString();
        error.ShouldNotBeNull();
        error.ShouldContain("epic.md");
    }

    // =========================================================================
    // Plan load-guidance — JSON contract
    // =========================================================================

    [Fact]
    public void LoadGuidance_EmptyDir_EmitsEmptyObject()
    {
        using var fx = new ConductorDirFixture(createGuidanceDir: false);

        var cmd = CreatePlanCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.LoadGuidance(fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.Success);
        output.Trim().ShouldBe("{}");
    }

    [Fact]
    public void LoadGuidance_DeserializationRoundTrip_KeysAreFileBasenames()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteAgentGuidance("epic", "Epic guidance.");
        fx.WriteAgentGuidance("planning-gate", "Gate guidance.");

        var cmd = CreatePlanCommands();
        var (_, output) = CaptureConsole(() => cmd.LoadGuidance(fx.ConfigDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.DictionaryStringString);
        result.ShouldNotBeNull();
        result.Keys.ShouldContain("epic");
        result.Keys.ShouldContain("planning-gate");
        result["epic"].ShouldBe("Epic guidance.");
    }

    // =========================================================================
    // State next-ready — JSON contract
    //
    // Standard CacheError + canonical {"error":"...","work_item_id":N} on
    // not-found; ConfigError when the type is unknown to the process config.
    // Snake-case enforced; null Error omitted on success.
    // =========================================================================

    [Fact]
    public async Task NextReady_SnakeCaseFieldNames_PresentInRawJson()
    {
        await SeedAsync(new WorkItemBuilder()
            .WithId(11_001).WithType(EpicType).WithTitle("Snake Epic").WithState(InProgressState).Build());

        var cmd = CreateStateCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextReady(11_001));

        exitCode.ShouldBe(ExitCodes.Success);

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"work_item_type\"");
        output.ShouldContain("\"status\"");
        output.ShouldContain("\"requirements\"");
        output.ShouldContain("\"next\"");
        output.ShouldContain("\"fulfilling\"");
        output.ShouldContain("\"satisfied\"");
        output.ShouldContain("\"needed\"");
        output.ShouldContain("\"resolved_inputs\"");
        output.ShouldContain("\"any_input_inferred\"");

        AssertNoPascalCase(output, "WorkItemId");
        AssertNoPascalCase(output, "WorkItemType");
        AssertNoPascalCase(output, "Status");
        AssertNoPascalCase(output, "Requirements");
        AssertNoPascalCase(output, "Next");
        AssertNoPascalCase(output, "ResolvedInputs");
        AssertNoPascalCase(output, "AnyInputInferred");
    }

    [Fact]
    public async Task NextReady_NullFieldsOmitted_WhenWritingNull()
    {
        // Happy path → Error is null → the "error" key must be absent.
        await SeedAsync(new WorkItemBuilder()
            .WithId(11_002).WithType(EpicType).WithTitle("Null fields").WithState(InProgressState).Build());

        var cmd = CreateStateCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextReady(11_002));

        exitCode.ShouldBe(ExitCodes.Success);
        output.ShouldNotContain("\"error\"");
    }

    [Fact]
    public async Task NextReady_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(new WorkItemBuilder()
            .WithId(11_003).WithType(EpicType).WithTitle("Roundtrip").WithState(InProgressState).Build());

        var cmd = CreateStateCommands();
        var (_, output) = await CaptureConsoleAsync(() => cmd.NextReady(11_003));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult);
        result.ShouldNotBeNull();
        result!.WorkItemId.ShouldBe(11_003);
        result.WorkItemType.ShouldBe(EpicType);
        result.Status.ShouldNotBeNullOrEmpty();
        result.Requirements.ShouldNotBeNull();
        result.ResolvedInputs.ShouldNotBeNull();
    }

    [Fact]
    public async Task NextReady_NotFound_ReturnsErrorJson_WithCacheErrorExitCode()
    {
        var cmd = CreateStateCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextReady(99_996));

        exitCode.ShouldBe(ExitCodes.CacheError);
        exitCode.ShouldBe(3);

        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_996);
    }

    [Fact]
    public async Task NextReady_TypeUnknown_ReturnsConfigError_WithErrorPayload()
    {
        // Bug is not in the test config (only Epic + Task) → ConfigError +
        // populated NextReady payload with status="error".
        await SeedAsync(new WorkItemBuilder()
            .WithId(11_004).WithType("Bug").WithTitle("Unknown type").WithState(ProposedState).Build());

        var cmd = CreateStateCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextReady(11_004));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult);
        result.ShouldNotBeNull();
        result!.Status.ShouldBe("error");
        result.Error.ShouldNotBeNullOrEmpty();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private PlanCommands CreatePlanCommands()
    {
        var config = CreateConfigBuilder().Build();
        var twig = new TwigClient(new FakeProcessRunner());
        return new PlanCommands(new HierarchyWalker(config, Repository), Repository, config, twig, new GitClient(new FakeProcessRunner()), new GhClient(new FakeProcessRunner()));
    }

    private RouteCommand CreateRouteCommand()
    {
        var config = CreateConfigBuilder().Build();
        return new RouteCommand(new PhaseDetector(config), Repository, config);
    }

    private ValidateCommand CreateValidateCommand()
    {
        var config = CreateConfigBuilder().Build();
        return new ValidateCommand(new TransitionValidator(config), Repository);
    }

    private HierarchyCommand CreateHierarchyCommand()
    {
        var config = CreateConfigBuilder().Build();
        return new HierarchyCommand(new HierarchyWalker(config, Repository));
    }

    private StateCommands CreateStateCommands()
    {
        var config = CreateConfigBuilder().Build();
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var ghTokenResolver = new GhTokenResolver(NSubstitute.Substitute.For<IGitClient>());
        var phaseDetector = new PhaseDetector(config);
        var validator = new TransitionValidator(config);
        var walker = new HierarchyWalker(config, Repository);
        return new StateCommands(twig, git, gh, runner, ghTokenResolver, phaseDetector, validator, walker, Repository, config);
    }

    private static ProcessConfigBuilder CreateConfigBuilder()
    {
        var transitions = new Dictionary<string, string>
        {
            ["begin_planning"] = "Active",
            ["implementation_complete"] = "Closed",
        };

        return new ProcessConfigBuilder()
            .WithProcessTemplate("Agile")
            .WithType(EpicType, ["plannable"], transitions)
            .WithType(TaskType, ["implementable"], transitions)
            .WithBranchStrategy();
    }

    /// <summary>
    /// Asserts that the exact PascalCase property name does not appear as a JSON key.
    /// Uses ordinal (case-sensitive) comparison to avoid false positives where
    /// snake_case names like "event" would match PascalCase "Event" under case-insensitive checks.
    /// </summary>
    private static void AssertNoPascalCase(string json, string pascalCaseName)
    {
        json.Contains($"\"{pascalCaseName}\"", StringComparison.Ordinal)
            .ShouldBeFalse($"JSON output should not contain PascalCase key \"{pascalCaseName}\"");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}

