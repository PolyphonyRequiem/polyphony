using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for the <c>polyphony plan</c> verb group. Verifies output shape,
/// routing-script exit-code convention (always 0), facet filtering for
/// <c>plan depth-guard</c> and <c>plan next-child</c>, and the file-IO loaders
/// <c>plan load-type</c> and <c>plan load-guidance</c>.
/// </summary>
public sealed class PlanCommandsTests : CommandTestBase
{
    private PlanCommands CreateCommand() =>
        new(new HierarchyWalker(Config, Repository), Repository, Config, new TwigClient(new FakeProcessRunner()));

    private PlanCommands CreateCommand(ProcessConfig config) =>
        new(new HierarchyWalker(config, Repository), Repository, config, new TwigClient(new FakeProcessRunner()));

    // ─────────────────────────────────────────────────────────────────────────
    // depth-guard
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DepthGuard_BelowCap_ReturnsAllowed()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.DepthGuard(depth: 2, maxDepth: 6));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.Allowed.ShouldBeTrue();
        result.Depth.ShouldBe(2);
        result.MaxDepth.ShouldBe(6);
        result.Remaining.ShouldBe(4);
        result.Message.ShouldBe("Depth 2 is within limit (max 6). 4 level(s) remaining.");
    }

    [Fact]
    public void DepthGuard_AtCap_ReturnsBlocked()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.DepthGuard(depth: 6, maxDepth: 6));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.Allowed.ShouldBeFalse();
        result.Remaining.ShouldBe(0);
        result.Message.ShouldBe("Recursion depth 6 reached maximum 6");
    }

    [Fact]
    public void DepthGuard_AboveCap_ReturnsBlocked()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.DepthGuard(depth: 9, maxDepth: 6));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public void DepthGuard_DefaultMaxDepth_IsSix()
    {
        // Mirrors the script default at scripts/depth-guard.ps1:22
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.DepthGuard(depth: 0));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.MaxDepth.ShouldBe(6);
        result.Allowed.ShouldBeTrue();
        result.Remaining.ShouldBe(6);
    }

    [Fact]
    public void DepthGuard_AlwaysExitsZero_RoutingScriptConvention()
    {
        // Routing scripts must never use exit code as a routing signal.
        var cmd = CreateCommand();

        var allowedExit = CaptureConsole(() => cmd.DepthGuard(0)).ExitCode;
        var blockedExit = CaptureConsole(() => cmd.DepthGuard(99)).ExitCode;

        allowedExit.ShouldBe(0);
        blockedExit.ShouldBe(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // next-child
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NextChild_FiltersToPlannableFacet()
    {
        // Default config: Epic and Issue are plannable; Task is not.
        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Root").WithState("New").Build();
        var issue1 = new WorkItemBuilder().WithId(101).WithType("Issue").WithTitle("Plannable Issue").WithState("New").WithParentId(100).Build();
        var task1 = new WorkItemBuilder().WithId(102).WithType("Task").WithTitle("Implementable Task").WithState("New").WithParentId(100).Build();
        var issue2 = new WorkItemBuilder().WithId(103).WithType("Issue").WithTitle("Another Issue").WithState("New").WithParentId(100).Build();
        await SeedAsync(epic, issue1, task1, issue2);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.HasPlannableChildren.ShouldBeTrue();
        result.Count.ShouldBe(2);
        result.ParentId.ShouldBe(100);
        result.PlannableChildren.Length.ShouldBe(2);
        result.PlannableChildren.Select(c => c.Id).ShouldBe(new[] { 101, 103 }, ignoreOrder: true);
        result.PlannableChildren.ShouldAllBe(c => c.Type == "Issue");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task NextChild_NoChildren_ReturnsEmpty()
    {
        var epic = new WorkItemBuilder().WithId(200).WithType("Epic").WithTitle("Childless").WithState("New").Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(200));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.HasPlannableChildren.ShouldBeFalse();
        result.Count.ShouldBe(0);
        result.PlannableChildren.ShouldBeEmpty();
        result.ParentId.ShouldBe(200);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task NextChild_OnlyImplementableChildren_ReturnsEmpty()
    {
        // All children are Tasks (not plannable), so result is empty.
        var epic = new WorkItemBuilder().WithId(300).WithType("Epic").WithTitle("Tasks Only").WithState("New").Build();
        var t1 = new WorkItemBuilder().WithId(301).WithType("Task").WithTitle("T1").WithState("New").WithParentId(300).Build();
        var t2 = new WorkItemBuilder().WithId(302).WithType("Task").WithTitle("T2").WithState("New").WithParentId(300).Build();
        await SeedAsync(epic, t1, t2);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(300));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.HasPlannableChildren.ShouldBeFalse();
        result.Count.ShouldBe(0);
        result.PlannableChildren.ShouldBeEmpty();
    }

    [Fact]
    public async Task NextChild_NotFound_ReturnsZeroExitWithErrorPayload()
    {
        // Routing-script convention: not-found is not a CacheError; emit empty payload + error.
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(99_999));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.HasPlannableChildren.ShouldBeFalse();
        result.Count.ShouldBe(0);
        result.PlannableChildren.ShouldBeEmpty();
        result.ParentId.ShouldBe(99_999);
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error.ShouldContain("99999");
    }

    [Fact]
    public async Task NextChild_PreservesChildOrder()
    {
        // Children seeded in arbitrary order should appear in the order returned by the repository.
        var epic = new WorkItemBuilder().WithId(400).WithType("Epic").WithTitle("Ordered").WithState("New").Build();
        var issueA = new WorkItemBuilder().WithId(401).WithType("Issue").WithTitle("A").WithState("New").WithParentId(400).Build();
        var issueB = new WorkItemBuilder().WithId(402).WithType("Issue").WithTitle("B").WithState("New").WithParentId(400).Build();
        var issueC = new WorkItemBuilder().WithId(403).WithType("Issue").WithTitle("C").WithState("New").WithParentId(400).Build();
        await SeedAsync(epic, issueA, issueB, issueC);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.NextChild(400));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.PlannableChildren.Length.ShouldBe(3);
        // Titles round-trip — ensures we're populating both id and title correctly.
        result.PlannableChildren.Select(c => c.Title).ShouldBe(new[] { "A", "B", "C" }, ignoreOrder: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // load-type
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadType_HappyPath_ReturnsDefinitionTemplateAndGuidance()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteTypeDefinition("issue", "# Issue type\nDescribes a unit of planning.");
        fx.WriteTypeTemplate("issue", "## Plan template\n- [ ] step");

        var configWithGuidance = new ProcessConfigBuilder()
            .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>
            {
                ["begin_planning"] = "Doing",
                ["implementation_complete"] = "Done",
            })
            .WithBranchStrategy()
            .Build();
        configWithGuidance.Types["Issue"].DecompositionGuidance = "Decompose into 2-5 tasks.";

        var item = new WorkItemBuilder().WithId(500).WithType("Issue").WithTitle("Item").WithState("New").Build();
        await SeedAsync(item);

        var cmd = CreateCommand(configWithGuidance);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.LoadType(500, fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanLoadTypeResult);
        result.ShouldNotBeNull();
        result.Type.ShouldBe("Issue");
        result.Definition.ShouldContain("Issue type");
        result.Template.ShouldContain("Plan template");
        result.DecompositionGuidance.ShouldBe("Decompose into 2-5 tasks.");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task LoadType_MissingTemplate_ReturnsEmptyTemplateString()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteTypeDefinition("issue", "# Issue");

        var item = new WorkItemBuilder().WithId(501).WithType("Issue").WithTitle("Item").WithState("New").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.LoadType(501, fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanLoadTypeResult);
        result.ShouldNotBeNull();
        result.Template.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task LoadType_WorkItemNotFound_ReturnsCacheErrorWithErrorField()
    {
        using var fx = new ConductorDirFixture();
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.LoadType(99_999, fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.CacheError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanLoadTypeResult);
        result.ShouldNotBeNull();
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error.ShouldContain("99999");
        // Defaults stand in for missing values
        result.Type.ShouldBe(string.Empty);
        result.Definition.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task LoadType_DefinitionMissing_ReturnsConfigError()
    {
        using var fx = new ConductorDirFixture();
        // No definition file written.

        var item = new WorkItemBuilder().WithId(502).WithType("Issue").WithTitle("Item").WithState("New").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.LoadType(502, fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanLoadTypeResult);
        result.ShouldNotBeNull();
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error.ShouldContain("issue.md");
        result.Type.ShouldBe("Issue"); // Type is known even if the file isn't
    }

    [Fact]
    public async Task LoadType_MultiwordType_SlugifiesWithDashes()
    {
        // "User Story" → "user-story.md" (matches script regex collapse).
        using var fx = new ConductorDirFixture();
        fx.WriteTypeDefinition("user-story", "# User Story");

        var configWithUserStory = new ProcessConfigBuilder()
            .WithType("User Story", ["plannable"], new Dictionary<string, string>
            {
                ["begin_planning"] = "Doing",
                ["implementation_complete"] = "Done",
            })
            .WithBranchStrategy()
            .Build();

        var item = new WorkItemBuilder().WithId(503).WithType("User Story").WithTitle("US-1").WithState("New").Build();
        await SeedAsync(item);

        var cmd = CreateCommand(configWithUserStory);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.LoadType(503, fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanLoadTypeResult);
        result.ShouldNotBeNull();
        result.Type.ShouldBe("User Story");
        result.Definition.ShouldContain("User Story");
    }

    [Fact]
    public async Task LoadType_SnakeCaseFieldNames_PresentInRawJson()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteTypeDefinition("issue", "# def");

        var item = new WorkItemBuilder().WithId(504).WithType("Issue").WithTitle("Item").WithState("New").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadType(504, fx.ConfigDir));

        output.ShouldContain("\"type\"");
        output.ShouldContain("\"definition\"");
        output.ShouldContain("\"template\"");
        output.ShouldContain("\"decomposition_guidance\"");
        output.ShouldNotContain("\"DecompositionGuidance\"");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // load-guidance
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadGuidance_HappyPath_ReturnsRoleMap()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteAgentGuidance("epic", "Epic guidance.");
        fx.WriteAgentGuidance("issue", "Issue guidance.");
        fx.WriteAgentGuidance("task", "Task guidance.");

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.LoadGuidance(fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.DictionaryStringString);
        result.ShouldNotBeNull();
        result.ShouldContainKey("epic");
        result.ShouldContainKey("issue");
        result.ShouldContainKey("task");
        result["epic"].ShouldBe("Epic guidance.");
        result["issue"].ShouldBe("Issue guidance.");
        result["task"].ShouldBe("Task guidance.");
    }

    [Fact]
    public void LoadGuidance_NoGuidanceDir_ReturnsEmptyObject()
    {
        // Guidance directory never created — must degrade gracefully.
        using var fx = new ConductorDirFixture(createGuidanceDir: false);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.LoadGuidance(fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.Success);
        output.Trim().ShouldBe("{}");
    }

    [Fact]
    public void LoadGuidance_EmptyGuidanceDir_ReturnsEmptyObject()
    {
        using var fx = new ConductorDirFixture();
        // Directory exists but no .md files.

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.LoadGuidance(fx.ConfigDir));

        exitCode.ShouldBe(ExitCodes.Success);
        output.Trim().ShouldBe("{}");
    }

    [Fact]
    public void LoadGuidance_IgnoresNonMarkdownFiles()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteAgentGuidance("epic", "MD content");
        fx.WriteRawGuidanceFile("README.txt", "Should be ignored");
        fx.WriteRawGuidanceFile("notes.json", "Should be ignored");

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.LoadGuidance(fx.ConfigDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.DictionaryStringString);
        result.ShouldNotBeNull();
        result.Keys.ShouldBe(["epic"]);
    }

    [Fact]
    public void LoadGuidance_DeterministicOrder_AlphabeticalByFilename()
    {
        using var fx = new ConductorDirFixture();
        fx.WriteAgentGuidance("zeta", "z");
        fx.WriteAgentGuidance("alpha", "a");
        fx.WriteAgentGuidance("mu", "m");

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.LoadGuidance(fx.ConfigDir));

        // Raw JSON should list keys in alphabetical order — JSON dicts preserve insertion order.
        var alphaIdx = output.IndexOf("\"alpha\"", StringComparison.Ordinal);
        var muIdx = output.IndexOf("\"mu\"", StringComparison.Ordinal);
        var zetaIdx = output.IndexOf("\"zeta\"", StringComparison.Ordinal);
        alphaIdx.ShouldBeGreaterThanOrEqualTo(0);
        muIdx.ShouldBeGreaterThan(alphaIdx);
        zetaIdx.ShouldBeGreaterThan(muIdx);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // review (review-router migration)
    // ─────────────────────────────────────────────────────────────────────────

    private static string ReviewerJson(int score, params string[] blockingIssues)
    {
        var issues = string.Join(",", blockingIssues.Select(i => $"\"{i}\""));
        return $$"""{"score":{{score}},"blocking_issues":[{{issues}}]}""";
    }

    [Fact]
    public void Review_HighScore_PassesByScore()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(95, "minor"), ReviewerJson(92, "nit"), priorCycleCount: 0));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanReviewResult);
        result.ShouldNotBeNull();
        result.Passed.ShouldBeTrue();
        result.ForcedByCap.ShouldBeFalse();
        result.AverageScore.ShouldBe(93); // (95 + 92) / 2 = 93
    }

    [Fact]
    public void Review_NoBlockingIssues_PassesByEmpty()
    {
        var cmd = CreateCommand();
        // Even with low scores, zero blocking issues passes.
        var (_, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(40), ReviewerJson(30), priorCycleCount: 0));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanReviewResult);
        result.ShouldNotBeNull();
        result.Passed.ShouldBeTrue();
        result.ForcedByCap.ShouldBeFalse();
        result.BlockingIssueCount.ShouldBe(0);
    }

    [Fact]
    public void Review_LowScoreWithBlocking_FailsWhenBelowCap()
    {
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(50, "needs work"), ReviewerJson(40, "missing acceptance criteria"), priorCycleCount: 1));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanReviewResult);
        result.ShouldNotBeNull();
        result.Passed.ShouldBeFalse();
        result.ForcedByCap.ShouldBeFalse();
        result.BlockingIssueCount.ShouldBe(2);
    }

    [Fact]
    public void Review_CapHit_PassesAsForcedByCap()
    {
        var cmd = CreateCommand();
        // Scores too low and blocking issues present, but cycle count hit cap.
        var (_, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(50, "issue"), ReviewerJson(40, "issue"), priorCycleCount: 5));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanReviewResult);
        result.ShouldNotBeNull();
        result.Passed.ShouldBeTrue();
        result.ForcedByCap.ShouldBeTrue();
        result.RevisionCyclesCompleted.ShouldBe(5);
    }

    [Fact]
    public void Review_CustomMaxCycles_ReplacesHardcodedDefault()
    {
        var cmd = CreateCommand();
        // Cap of 3 — cycle count of 3 should force.
        var (_, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(50, "issue"), ReviewerJson(40, "issue"), priorCycleCount: 3, maxCycles: 3));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanReviewResult);
        result.ShouldNotBeNull();
        result.Passed.ShouldBeTrue();
        result.ForcedByCap.ShouldBeTrue();
    }

    [Fact]
    public void Review_BlockingIssues_RenderedInCombinedFeedback()
    {
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(60, "tech-issue-1"), ReviewerJson(70, "read-issue-1"), priorCycleCount: 0));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanReviewResult);
        result.ShouldNotBeNull();
        result.CombinedFeedback.ShouldContain("technical reviewer (score: 60)");
        result.CombinedFeedback.ShouldContain("- tech-issue-1");
        result.CombinedFeedback.ShouldContain("readability reviewer (score: 70)");
        result.CombinedFeedback.ShouldContain("- read-issue-1");
    }

    [Fact]
    public void Review_NoBlocking_CombinedFeedbackIsEmpty()
    {
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(95), ReviewerJson(95), priorCycleCount: 0));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanReviewResult);
        result.ShouldNotBeNull();
        result.CombinedFeedback.ShouldBeEmpty();
    }

    [Fact]
    public void Review_AlwaysReturnsSuccess_RoutingScriptConvention()
    {
        // Routing scripts always exit 0; the workflow routes on JSON payload.
        var cmd = CreateCommand();

        var (passExit, _) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(95), ReviewerJson(95), priorCycleCount: 0));
        var (failExit, _) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(40, "x"), ReviewerJson(40, "x"), priorCycleCount: 0));
        var (capExit, _) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(40, "x"), ReviewerJson(40, "x"), priorCycleCount: 5));

        passExit.ShouldBe(ExitCodes.Success);
        failExit.ShouldBe(ExitCodes.Success);
        capExit.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public void Review_SnakeCaseFieldNames_PresentInRawJson()
    {
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(95), ReviewerJson(95), priorCycleCount: 0));

        output.ShouldContain("\"average_score\"");
        output.ShouldContain("\"technical_score\"");
        output.ShouldContain("\"readability_score\"");
        output.ShouldContain("\"revision_cycles_completed\"");
        output.ShouldContain("\"blocking_issue_count\"");
        output.ShouldContain("\"combined_feedback\"");
        output.ShouldContain("\"passed\"");
        output.ShouldContain("\"forced_by_cap\"");
        output.ShouldNotContain("\"AverageScore\"");
    }

    [Fact]
    public void Review_ScoreFloorMatchesPowerShellIntegerDivision()
    {
        // PowerShell: [math]::Floor((95 + 92) / 2) = 93 (int math, not float)
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() =>
            cmd.Review(ReviewerJson(95), ReviewerJson(92), priorCycleCount: 0));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanReviewResult);
        result.ShouldNotBeNull();
        result.AverageScore.ShouldBe(93);
    }
}

/// <summary>
/// Disposable fixture for a temporary <c>.conductor</c> directory used by
/// <c>load-type</c> and <c>load-guidance</c> tests.
/// </summary>
internal sealed class ConductorDirFixture : IDisposable
{
    public string ConfigDir { get; }

    public ConductorDirFixture(bool createGuidanceDir = true)
    {
        ConfigDir = Path.Combine(Path.GetTempPath(), $"polyphony-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(ConfigDir, "work-item-types", "templates"));
        if (createGuidanceDir)
            Directory.CreateDirectory(Path.Combine(ConfigDir, "agent-guidance"));
    }

    public void WriteTypeDefinition(string slug, string content) =>
        File.WriteAllText(Path.Combine(ConfigDir, "work-item-types", $"{slug}.md"), content);

    public void WriteTypeTemplate(string slug, string content) =>
        File.WriteAllText(Path.Combine(ConfigDir, "work-item-types", "templates", $"{slug}-template.md"), content);

    public void WriteAgentGuidance(string role, string content) =>
        File.WriteAllText(Path.Combine(ConfigDir, "agent-guidance", $"{role}.md"), content);

    public void WriteRawGuidanceFile(string fileName, string content) =>
        File.WriteAllText(Path.Combine(ConfigDir, "agent-guidance", fileName), content);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(ConfigDir))
                Directory.Delete(ConfigDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}


