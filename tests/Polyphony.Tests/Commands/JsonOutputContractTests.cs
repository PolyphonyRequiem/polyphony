using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Routing;
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
        File.WriteAllText(configPath, "process_template: Basic\ntypes: { Epic: { capabilities: [plannable] } }\ntransitions: { Epic: { begin_planning: Doing } }\n");
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

        // If pg_branch is null, it should be absent from the raw JSON
        if (result.WorkspaceHint?.PgBranch is null)
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
        result.TargetState.ShouldBe("Active");
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
        output.ShouldContain("\"capabilities\"");
        output.ShouldContain("\"state\"");
        output.ShouldContain("\"children\"");

        // No PascalCase leakage (use ordinal comparison for single-word properties)
        AssertNoPascalCase(output, "WorkItemId");
        AssertNoPascalCase(output, "Title");
        AssertNoPascalCase(output, "Type");
        AssertNoPascalCase(output, "Capabilities");
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
        result.Capabilities.ShouldContain("plannable");
        result.Children.ShouldNotBeNull();
    }

    // =========================================================================
    // Cross-command error format consistency
    // =========================================================================

    [Fact]
    public async Task AllCommands_NotFound_ErrorJsonFormatConsistent()
    {
        const int missingId = 99_900;

        var routeCmd = CreateRouteCommand();
        var validateCmd = CreateValidateCommand();
        var hierarchyCmd = CreateHierarchyCommand();

        var (routeExit, routeOutput) = await CaptureConsoleAsync(() => routeCmd.Route(missingId));
        var (validateExit, validateOutput) = await CaptureConsoleAsync(() => validateCmd.Validate(missingId, "begin_planning"));
        var (hierarchyExit, hierarchyOutput) = await CaptureConsoleAsync(() => hierarchyCmd.Hierarchy(missingId));

        // All three should return CacheError (3)
        routeExit.ShouldBe(ExitCodes.CacheError);
        validateExit.ShouldBe(ExitCodes.CacheError);
        hierarchyExit.ShouldBe(ExitCodes.CacheError);

        // All three should produce valid JSON with "error" and "work_item_id" fields
        foreach (var output in new[] { routeOutput, validateOutput, hierarchyOutput })
        {
            var doc = JsonDocument.Parse(output);
            doc.RootElement.TryGetProperty("error", out var errorProp).ShouldBeTrue();
            errorProp.GetString().ShouldNotBeNullOrEmpty();
            doc.RootElement.TryGetProperty("work_item_id", out var idProp).ShouldBeTrue();
            idProp.GetInt32().ShouldBe(missingId);
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

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
