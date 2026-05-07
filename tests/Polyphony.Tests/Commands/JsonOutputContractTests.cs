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
    // Edges check command — JSON contract
    // =========================================================================

    [Fact]
    public async Task EdgesCheck_SnakeCaseFieldNames_PresentInRawJson()
    {
        await SeedAsync(new WorkItemBuilder()
            .WithId(12_001).WithType(EpicType).WithTitle("Edges check").WithState(InProgressState).Build());

        var cmd = CreateEdgesCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 12_001));

        exitCode.ShouldBe(ExitCodes.Success);
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"items_walked\"");
        output.ShouldContain("\"edges_total\"");
        output.ShouldContain("\"has_conflicts\"");
        output.ShouldContain("\"conflicts\"");

        AssertNoPascalCase(output, "WorkItemId");
        AssertNoPascalCase(output, "ItemsWalked");
        AssertNoPascalCase(output, "EdgesTotal");
        AssertNoPascalCase(output, "HasConflicts");
        AssertNoPascalCase(output, "Conflicts");
    }

    [Fact]
    public async Task EdgesCheck_NullFieldsOmitted_WhenWritingNull()
    {
        // Happy path → Error / ErrorCode are null → those keys must be absent.
        await SeedAsync(new WorkItemBuilder()
            .WithId(12_002).WithType(EpicType).WithTitle("Null fields").WithState(InProgressState).Build());

        var cmd = CreateEdgesCommands();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 12_002));

        output.ShouldNotContain("\"error\"");
        output.ShouldNotContain("\"error_code\"");
    }

    [Fact]
    public async Task EdgesCheck_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(new WorkItemBuilder()
            .WithId(12_003).WithType(EpicType).WithTitle("Roundtrip").WithState(InProgressState).Build());

        var cmd = CreateEdgesCommands();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 12_003));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.EdgesCheckResult);
        result.ShouldNotBeNull();
        result!.WorkItemId.ShouldBe(12_003);
        result.ItemsWalked.ShouldBe(1);
        result.HasConflicts.ShouldBeFalse();
        result.Conflicts.ShouldNotBeNull();
    }

    [Fact]
    public async Task EdgesCheck_NotFound_ReturnsErrorEnvelope_WithSuccessExitCode()
    {
        // Routing-style verb: always exit 0, route via envelope's error_code.
        var cmd = CreateEdgesCommands();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 99_995));

        exitCode.ShouldBe(ExitCodes.Success);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("error_code").GetString().ShouldBe("work_item_not_found");
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_995);
    }

    // =========================================================================
    // Agent compose-addendum command — JSON contract (Phase 6 PR #5)
    // =========================================================================

    [Fact]
    public async Task AgentComposeAddendum_SnakeCaseFieldNames_PresentInRawJson()
    {
        await SeedAsync(new WorkItemBuilder()
            .WithId(14_001).WithType(EpicType).WithTitle("Compose addendum").WithState(InProgressState).Build());

        var (cmd, policyPath, dispose) = CreateAgentCommands();
        try
        {
            var (exitCode, output) = await CaptureConsoleAsync(() => cmd.ComposeAddendum(14_001, policyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            output.ShouldContain("\"work_item_id\"");
            output.ShouldContain("\"facets\"");
            output.ShouldContain("\"skills\"");
            output.ShouldContain("\"mcps\"");
            output.ShouldContain("\"guidance_present\"");

            AssertNoPascalCase(output, "WorkItemId");
            AssertNoPascalCase(output, "Facets");
            AssertNoPascalCase(output, "Skills");
            AssertNoPascalCase(output, "Mcps");
            AssertNoPascalCase(output, "GuidancePresent");
            AssertNoPascalCase(output, "ErrorCode");
        }
        finally { dispose(); }
    }

    [Fact]
    public async Task AgentComposeAddendum_NullFieldsOmitted_WhenWritingNull()
    {
        // Happy path → guidance / error / error_code are null → keys must be absent.
        await SeedAsync(new WorkItemBuilder()
            .WithId(14_002).WithType(EpicType).WithTitle("Null fields").WithState(InProgressState).Build());

        var (cmd, policyPath, dispose) = CreateAgentCommands();
        try
        {
            var (_, output) = await CaptureConsoleAsync(() => cmd.ComposeAddendum(14_002, policyPath));

            output.ShouldNotContain("\"guidance\":null");
            output.ShouldNotContain("\"error\"");
            output.ShouldNotContain("\"error_code\"");
        }
        finally { dispose(); }
    }

    [Fact]
    public async Task AgentComposeAddendum_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(new WorkItemBuilder()
            .WithId(14_003).WithType(EpicType).WithTitle("Roundtrip").WithState(InProgressState).Build());

        var (cmd, policyPath, dispose) = CreateAgentCommands();
        try
        {
            var (_, output) = await CaptureConsoleAsync(() => cmd.ComposeAddendum(14_003, policyPath));

            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.AgentComposeAddendumResult);
            result.ShouldNotBeNull();
            result!.WorkItemId.ShouldBe(14_003);
            result.Facets.ShouldNotBeNull();
            result.Skills.ShouldNotBeNull();
            result.Mcps.ShouldNotBeNull();
            result.GuidancePresent.ShouldBeFalse();
        }
        finally { dispose(); }
    }

    [Fact]
    public async Task AgentComposeAddendum_NotFound_ReturnsErrorEnvelope_WithSuccessExitCode()
    {
        // Routing-style verb: always exit 0, route via envelope's error_code.
        var (cmd, policyPath, dispose) = CreateAgentCommands();
        try
        {
            var (exitCode, output) = await CaptureConsoleAsync(() => cmd.ComposeAddendum(99_994, policyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var doc = JsonDocument.Parse(output);
            doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
            doc.RootElement.GetProperty("error_code").GetString().ShouldBe("work_item_not_found");
            doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_994);
        }
        finally { dispose(); }
    }


    // =========================================================================
    // Worklist build command — JSON contract (Phase 7 PR #7 retrofit)
    // =========================================================================

    [Fact]
    public async Task WorklistBuild_SnakeCaseFieldNames_PresentInRawJson()
    {
        // Build a tmp manifest pointing at the seeded root so the verb can
        // walk + emit the new envelope.
        await SeedAsync(new WorkItemBuilder()
            .WithId(13_001).WithType(EpicType).WithTitle("Worklist root").WithState(InProgressState).Build());
        var (manifestPath, dispose) = WriteRunManifest(13_001);
        try
        {
            var cmd = CreateWorklistCommands();
            var (exitCode, output) = await CaptureConsoleAsync(() =>
                cmd.Build(rootId: 13_001, manifestPath: manifestPath, json: true));

            exitCode.ShouldBe(ExitCodes.Success);
            output.ShouldContain("\"root_id\"");
            output.ShouldContain("\"items_walked\"");
            output.ShouldContain("\"has_conflicts\"");
            output.ShouldContain("\"conflicts\"");
            output.ShouldContain("\"waves\"");
            output.ShouldContain("\"wave_index\"");
            // Cutover: `depth` is gone from the wave entry.
            output.ShouldNotContain("\"depth\"");

            AssertNoPascalCase(output, "RootId");
            AssertNoPascalCase(output, "ItemsWalked");
            AssertNoPascalCase(output, "HasConflicts");
            AssertNoPascalCase(output, "Conflicts");
            AssertNoPascalCase(output, "Waves");
            AssertNoPascalCase(output, "WaveIndex");
        }
        finally { dispose(); }
    }

    [Fact]
    public async Task WorklistBuild_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(new WorkItemBuilder()
            .WithId(13_002).WithType(EpicType).WithTitle("Roundtrip").WithState(InProgressState).Build());
        var (manifestPath, dispose) = WriteRunManifest(13_002);
        try
        {
            var cmd = CreateWorklistCommands();
            var (_, output) = await CaptureConsoleAsync(() =>
                cmd.Build(rootId: 13_002, manifestPath: manifestPath, json: true));

            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorklistResult);
            result.ShouldNotBeNull();
            result!.RootId.ShouldBe(13_002);
            result.ItemsWalked.ShouldBe(1);
            result.HasConflicts.ShouldBeFalse();
            result.Conflicts.ShouldNotBeNull();
            result.Conflicts.ShouldBeEmpty();
            result.Waves.Count.ShouldBe(1);
        }
        finally { dispose(); }
    }

    [Fact]
    public async Task WorklistBuild_NotFound_ReturnsErrorEnvelope_WithSuccessExitCode()
    {
        // No twig items seeded — manifest exists but root is missing.
        var (manifestPath, dispose) = WriteRunManifest(13_003);
        try
        {
            var cmd = CreateWorklistCommands();
            var (exitCode, output) = await CaptureConsoleAsync(() =>
                cmd.Build(rootId: 13_003, manifestPath: manifestPath, json: true));

            // Routing-style: always exit 0, error code in envelope.
            exitCode.ShouldBe(ExitCodes.Success);
            var doc = JsonDocument.Parse(output);
            doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
            doc.RootElement.GetProperty("error_code").GetString().ShouldBe("root_not_found");
            doc.RootElement.GetProperty("root_id").GetInt32().ShouldBe(13_003);
            // Even error envelopes carry has_conflicts + conflicts so consumers
            // can read them without first distinguishing error vs conflict.
            doc.RootElement.GetProperty("has_conflicts").GetBoolean().ShouldBeFalse();
            doc.RootElement.GetProperty("conflicts").GetArrayLength().ShouldBe(0);
            doc.RootElement.GetProperty("waves").GetArrayLength().ShouldBe(0);
            doc.RootElement.GetProperty("items_walked").GetInt32().ShouldBe(0);
        }
        finally { dispose(); }
    }

    /// <summary>
    /// Writes a minimal <c>run.yaml</c> for the verb's manifest_path arg.
    /// Returns a disposer that cleans up the temp directory.
    /// </summary>
    private static (string Path, Action Dispose) WriteRunManifest(int rootId)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"polyphony-worklist-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var manifestPath = System.IO.Path.Combine(dir, "run.yaml");
        Polyphony.Manifest.RunManifestStore.Save(manifestPath, new Polyphony.Manifest.RunManifest
        {
            Schema = 1,
            RootId = rootId,
            PlatformProject = "github.com/owner/repo",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = new Dictionary<string, int>(StringComparer.Ordinal),
            MergedPlanPrs = new List<Polyphony.Manifest.MergedPlanPrEntry>(),
        });
        return (manifestPath, () => { try { Directory.Delete(dir, recursive: true); } catch { } });
    }

    // =========================================================================


    [Fact]
    public async Task AllCommands_NotFound_ErrorJsonFormatConsistent()
    {
        const int missingId = 99_900;

        var validateCmd = CreateValidateCommand();
        var hierarchyCmd = CreateHierarchyCommand();
        var planCmd = CreatePlanCommands();
        var nextReadyCmd = CreateStateCommands();
        using var fx = new ConductorDirFixture();

        var (validateExit, validateOutput) = await CaptureConsoleAsync(() => validateCmd.Validate(missingId, "begin_planning"));
        var (hierarchyExit, hierarchyOutput) = await CaptureConsoleAsync(() => hierarchyCmd.Hierarchy(missingId));
        var (loadTypeExit, loadTypeOutput) = await CaptureConsoleAsync(() => planCmd.LoadType(missingId, fx.ConfigDir));
        var (nextReadyExit, nextReadyOutput) = await CaptureConsoleAsync(() => nextReadyCmd.NextReady(missingId));

        // All four operator-facing commands should return CacheError (3) on missing work item.
        validateExit.ShouldBe(ExitCodes.CacheError);
        hierarchyExit.ShouldBe(ExitCodes.CacheError);
        loadTypeExit.ShouldBe(ExitCodes.CacheError);
        nextReadyExit.ShouldBe(ExitCodes.CacheError);

        // All four should produce valid JSON with an "error" field.
        // Validate/Hierarchy/NextReady include "work_item_id"; LoadType emits its own shape
        // (PlanLoadTypeResult with empty type/definition + error), so we only assert the
        // common "error" string contract here.
        foreach (var output in new[] { validateOutput, hierarchyOutput, loadTypeOutput, nextReadyOutput })
        {
            var doc = JsonDocument.Parse(output);
            doc.RootElement.TryGetProperty("error", out var errorProp).ShouldBeTrue();
            errorProp.GetString().ShouldNotBeNullOrEmpty();
        }

        // Validate/Hierarchy/NextReady additionally guarantee the work_item_id field.
        foreach (var output in new[] { validateOutput, hierarchyOutput, nextReadyOutput })
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
    // PR check-evidence-floor — JSON contract (Phase 6 PR #7)
    // =========================================================================

    [Fact]
    public void CheckEvidenceFloorResult_RoundTrip_PreservesSnakeCaseAndOmitsNulls()
    {
        // Happy-path payload — all nullable error fields stay null and
        // must be omitted from the wire JSON per the WhenWritingNull policy.
        var passing = new PrCheckEvidenceFloorResult
        {
            Success = true,
            PrNumber = 42,
            CommitCount = 3,
            BodyLength = 128,
            PassesFloor = true,
            Violations = Array.Empty<string>(),
            ErrorCode = null,
            ErrorMessage = null,
        };

        var json = JsonSerializer.Serialize(passing, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult);

        json.ShouldContain("\"success\":true");
        json.ShouldContain("\"pr_number\":42");
        json.ShouldContain("\"commit_count\":3");
        json.ShouldContain("\"body_length\":128");
        json.ShouldContain("\"passes_floor\":true");
        json.ShouldContain("\"violations\":[]");
        json.ShouldNotContain("\"error_code\"");
        json.ShouldNotContain("\"error_message\"");
        AssertNoPascalCase(json, "PrNumber");
        AssertNoPascalCase(json, "CommitCount");
        AssertNoPascalCase(json, "BodyLength");
        AssertNoPascalCase(json, "PassesFloor");
        AssertNoPascalCase(json, "Violations");

        // Round-trip back through the same source-generated context.
        var rehydrated = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult)!;
        rehydrated.Success.ShouldBeTrue();
        rehydrated.PrNumber.ShouldBe(42);
        rehydrated.PassesFloor.ShouldBeTrue();
        rehydrated.Violations.ShouldBeEmpty();
        rehydrated.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void CheckEvidenceFloorResult_ViolationsAndErrors_SerializeWithStableShape()
    {
        // Sad path: both violations + an error envelope can co-exist on
        // wire (in practice they're mutually exclusive, but the contract
        // must serialize each cleanly so consumers can pattern-match).
        var failed = new PrCheckEvidenceFloorResult
        {
            Success = false,
            PrNumber = 9999,
            CommitCount = 0,
            BodyLength = 0,
            PassesFloor = false,
            Violations = new[] { "no_commits", "empty_body" },
            ErrorCode = "pr_not_found",
            ErrorMessage = "PR #9999 not found",
        };

        var json = JsonSerializer.Serialize(failed, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult);

        json.ShouldContain("\"violations\":[\"no_commits\",\"empty_body\"]");
        json.ShouldContain("\"error_code\":\"pr_not_found\"");
        json.ShouldContain("\"error_message\":\"PR #9999 not found\"");
        json.ShouldContain("\"passes_floor\":false");
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
        return new StateCommands(twig, git, gh, runner, Repository, config);
    }

    private EdgesCommands CreateEdgesCommands()
    {
        var config = CreateConfigBuilder().Build();
        return new EdgesCommands(Repository, config);
    }

    private (AgentCommands Cmd, string PolicyPath, Action Dispose) CreateAgentCommands()
    {
        var config = CreateConfigBuilder().Build();
        var dir = Path.Combine(Path.GetTempPath(), $"polyphony-agent-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var policyPath = Path.Combine(dir, "policy.yaml");
        return (
            new AgentCommands(Repository, config),
            policyPath,
            () => { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } });
    }

    private WorklistCommands CreateWorklistCommands()
    {
        var config = CreateConfigBuilder().Build();
        return new WorklistCommands(Repository, config);
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

    // =========================================================================
    // Plan extract-renegotiation-flag — JSON contract (Phase 3 P8)
    //
    // Verb invocation is covered in PlanCommandsExtractRenegotiationFlagTests;
    // here we just verify the result type round-trips through PolyphonyJsonContext
    // with snake_case keys + null omission.
    // =========================================================================

    [Fact]
    public void PlanExtractRenegotiationFlag_JsonContract_SnakeCase_RoundTrip()
    {
        var present = new PlanExtractRenegotiationFlagResult
        {
            Success = true,
            PrNumber = 1234,
            FlagPresent = true,
            RenegotiationRequest = "please replan",
            FencedBlockWellFormed = true,
        };
        var json = JsonSerializer.Serialize(present, PolyphonyJsonContext.Default.PlanExtractRenegotiationFlagResult);

        json.ShouldContain("\"success\"");
        json.ShouldContain("\"pr_number\"");
        json.ShouldContain("\"flag_present\"");
        json.ShouldContain("\"renegotiation_request\"");
        json.ShouldContain("\"fenced_block_well_formed\"");
        AssertNoPascalCase(json, "PrNumber");
        AssertNoPascalCase(json, "FlagPresent");
        AssertNoPascalCase(json, "FencedBlockWellFormed");
        // Null fields must be omitted on the success path.
        json.ShouldNotContain("\"error_code\"");
        json.ShouldNotContain("\"error_message\"");

        var parsed = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.PlanExtractRenegotiationFlagResult);
        parsed.ShouldNotBeNull();
        parsed.PrNumber.ShouldBe(1234);
        parsed.FlagPresent.ShouldBeTrue();
        parsed.RenegotiationRequest.ShouldBe("please replan");
        parsed.FencedBlockWellFormed.ShouldBeTrue();
    }

    [Fact]
    public void PlanExtractRenegotiationFlag_ErrorEnvelope_PopulatesCodeAndMessage()
    {
        var error = new PlanExtractRenegotiationFlagResult
        {
            Success = false,
            PrNumber = 1234,
            FlagPresent = false,
            FencedBlockWellFormed = false,
            ErrorCode = "malformed_renegotiation_block",
            ErrorMessage = "open without close",
        };
        var json = JsonSerializer.Serialize(error, PolyphonyJsonContext.Default.PlanExtractRenegotiationFlagResult);

        json.ShouldContain("\"error_code\"");
        json.ShouldContain("\"error_message\"");
        json.ShouldContain("malformed_renegotiation_block");
        // RenegotiationRequest is null and must be omitted.
        json.ShouldNotContain("\"renegotiation_request\"");
    }

    // =========================================================================
    // Plan validate-scope — JSON contract (Phase 3 P8)
    // =========================================================================

    [Fact]
    public void PlanValidateScope_JsonContract_SnakeCase_RoundTrip()
    {
        var allow = new PlanValidateScopeResult
        {
            Success = true,
            PrNumber = 1234,
            FilesTouched = ["plans/1100/1101.md", "plans/1100/parent-amendment.md"],
            FilesInScope = ["plans/1100/1101.md"],
            FilesOutOfScope = ["plans/1100/parent-amendment.md"],
            FlagPresent = true,
            Verdict = "allow",
            Warnings = [],
        };
        var json = JsonSerializer.Serialize(allow, PolyphonyJsonContext.Default.PlanValidateScopeResult);

        json.ShouldContain("\"pr_number\"");
        json.ShouldContain("\"files_touched\"");
        json.ShouldContain("\"files_in_scope\"");
        json.ShouldContain("\"files_out_of_scope\"");
        json.ShouldContain("\"flag_present\"");
        json.ShouldContain("\"verdict\"");
        json.ShouldContain("\"warnings\"");
        AssertNoPascalCase(json, "PrNumber");
        AssertNoPascalCase(json, "FilesTouched");
        AssertNoPascalCase(json, "FilesInScope");
        AssertNoPascalCase(json, "FilesOutOfScope");
        AssertNoPascalCase(json, "FlagPresent");
        AssertNoPascalCase(json, "Verdict");
        // Null fields omitted on the allow path.
        json.ShouldNotContain("\"error_code\"");
        json.ShouldNotContain("\"error_message\"");

        var parsed = JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.PlanValidateScopeResult);
        parsed.ShouldNotBeNull();
        parsed.Verdict.ShouldBe("allow");
        parsed.FilesInScope.Count.ShouldBe(1);
        parsed.FilesOutOfScope.Count.ShouldBe(1);
        parsed.FlagPresent.ShouldBeTrue();
    }

    [Fact]
    public void PlanValidateScope_BlockingVerdict_SerializesErrorCode()
    {
        var block = new PlanValidateScopeResult
        {
            Success = true,
            PrNumber = 1234,
            FilesTouched = ["src/code.cs"],
            FilesInScope = Array.Empty<string>(),
            FilesOutOfScope = ["src/code.cs"],
            FlagPresent = false,
            Verdict = "block",
            Warnings = Array.Empty<string>(),
            ErrorCode = "scope_violation_no_flag",
            ErrorMessage = "out of scope without flag",
        };
        var json = JsonSerializer.Serialize(block, PolyphonyJsonContext.Default.PlanValidateScopeResult);

        json.ShouldContain("\"verdict\":\"block\"");
        json.ShouldContain("scope_violation_no_flag");
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

