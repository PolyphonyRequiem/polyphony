using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class StateCommandsDetectTests : CommandTestBase
{
    private readonly string _planRoot;

    public StateCommandsDetectTests()
    {
        _planRoot = Path.Combine(Path.GetTempPath(), "polyphony-state-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planRoot);
    }

    private (StateCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var phaseDetector = new PhaseDetector(Config);
        var validator = new TransitionValidator(Config);
        var walker = new HierarchyWalker(Config, Repository);
        // Stub no-op responses for twig side-effect calls (sync/set/state).
        // Reads (config/show/version) MUST be stubbed explicitly per test.
        runner.WhenStartsWith("twig", new[] { "sync" }, new ProcessResult(0, "{}", ""));
        runner.WhenStartsWith("twig", new[] { "set" }, new ProcessResult(0, "{}", ""));
        runner.WhenStartsWith("twig", new[] { "state" }, new ProcessResult(0, "{}", ""));
        return (new StateCommands(twig, git, gh, runner, phaseDetector, validator, walker, Repository, Config), runner);
    }

    private static void StubTwigConfig(FakeProcessRunner runner, string key, string? value)
    {
        var json = value is null ? "{}" : $$"""{"info":"{{value}}"}""";
        runner.WhenExact("twig", new[] { "config", key, "--output", "json" },
            new ProcessResult(value is null ? 1 : 0, json, ""));
    }

    [Fact]
    public async Task Detect_InvalidIntent_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Detect(workItem: 100, intent: "garbage"));
        exit.ShouldBe(ExitCodes.ConfigError);
        output.ShouldContain("\"error\"");
    }

    [Fact]
    public async Task Detect_WorkItemNotFound_ReturnsCacheError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Detect(workItem: 9999, planRoot: _planRoot));
        exit.ShouldBe(ExitCodes.CacheError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateDetectResult)!;
        result.Phase.ShouldBe("error");
        result.Error.ShouldContain("9999");
    }

    [Fact]
    public async Task Detect_NoChildrenNoPlan_ReportsUnseededAndNeedsPlanning()
    {
        var item = new WorkItemBuilder().WithId(100).WithType("Issue").WithTitle("Test").WithState("To Do").Build();
        await SeedAsync(item);

        var (cmd, runner) = CreateCommand();
        StubTwigConfig(runner, "organization", "myorg");
        StubTwigConfig(runner, "project", "myproj");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Detect(workItem: 100, planRoot: _planRoot));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateDetectResult)!;
        result.WorkItemId.ShouldBe(100);
        result.SeedStatus.ShouldBe("unseeded");
        result.HasSeededChildren.ShouldBeFalse();
        result.HasPlan.ShouldBeFalse();
        result.PlanStatus.ShouldBe("none");
        result.AdoOrg.ShouldBe("myorg");
        result.AdoProject.ShouldBe("myproj");
        result.AdoWorkspace.ShouldBe("myorg/myproj");
        result.Intent.ShouldBe("resume");
        result.Error.ShouldBeEmpty($"Output was: {output}");
    }

    [Fact]
    public async Task Detect_PlanFileMatchesViaFrontmatter_DiscoveredAsComplete()
    {
        var planFile = Path.Combine(_planRoot, "test-feature.plan.md");
        File.WriteAllText(planFile, "---\nwork_item_id: 100\nstatus: draft\n---\n# Plan body");

        var item = new WorkItemBuilder().WithId(100).WithType("Issue").WithTitle("Test").WithState("To Do").Build();
        await SeedAsync(item);

        var (cmd, runner) = CreateCommand();
        StubTwigConfig(runner, "organization", "");
        StubTwigConfig(runner, "project", "");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Detect(workItem: 100, planRoot: _planRoot));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateDetectResult)!;

        result.HasPlan.ShouldBeTrue($"Output was: {output}");
        result.PlanStatus.ShouldBe("complete");
        result.PlanSource.ShouldBe("filesystem_fallback");
        result.PlanPath.ShouldEndWith("test-feature.plan.md");
    }

    [Fact]
    public async Task Detect_AmbiguousPlan_SetsAmbiguousAndErrorMessage()
    {
        File.WriteAllText(Path.Combine(_planRoot, "a.plan.md"), "---\nwork_item_id: 100\n---\n");
        File.WriteAllText(Path.Combine(_planRoot, "b.plan.md"), "---\nwork_item_id: 100\n---\n");

        var item = new WorkItemBuilder().WithId(100).WithType("Issue").WithTitle("X").WithState("To Do").Build();
        await SeedAsync(item);

        var (cmd, runner) = CreateCommand();
        StubTwigConfig(runner, "organization", "o");
        StubTwigConfig(runner, "project", "p");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Detect(workItem: 100, planRoot: _planRoot));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateDetectResult)!;

        result.PlanStatus.ShouldBe("ambiguous");
        result.HasPlan.ShouldBeFalse();
        result.Error.ShouldContain("ambiguous", Case.Insensitive);
    }

    [Fact]
    public async Task Detect_IntentNew_ConflictsWhenPlanExists()
    {
        File.WriteAllText(Path.Combine(_planRoot, "x.plan.md"), "---\nwork_item_id: 100\n---\n");
        var item = new WorkItemBuilder().WithId(100).WithType("Issue").WithTitle("X").WithState("To Do").Build();
        await SeedAsync(item);

        var (cmd, runner) = CreateCommand();
        StubTwigConfig(runner, "organization", "o");
        StubTwigConfig(runner, "project", "p");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Detect(workItem: 100, intent: "new", planRoot: _planRoot));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateDetectResult)!;

        result.IntentConflict.ShouldBeTrue($"Output was: {output}");
        result.NeedsCleanup.ShouldBeFalse();
    }

    [Fact]
    public async Task Detect_IntentRedo_NeedsCleanupWhenPlanExists()
    {
        File.WriteAllText(Path.Combine(_planRoot, "x.plan.md"), "---\nwork_item_id: 100\n---\n");
        var item = new WorkItemBuilder().WithId(100).WithType("Issue").WithTitle("X").WithState("To Do").Build();
        await SeedAsync(item);

        var (cmd, runner) = CreateCommand();
        StubTwigConfig(runner, "organization", "o");
        StubTwigConfig(runner, "project", "p");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Detect(workItem: 100, intent: "redo", planRoot: _planRoot));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateDetectResult)!;

        result.NeedsCleanup.ShouldBeTrue($"Output was: {output}");
        result.IntentConflict.ShouldBeFalse();
    }

    [Fact]
    public async Task Detect_OutputIsSnakeCaseJson()
    {
        var item = new WorkItemBuilder().WithId(100).WithType("Issue").WithTitle("X").WithState("To Do").Build();
        await SeedAsync(item);

        var (cmd, runner) = CreateCommand();
        StubTwigConfig(runner, "organization", "o");
        StubTwigConfig(runner, "project", "p");

        var (_, output) = await CaptureConsoleAsync(() => cmd.Detect(workItem: 100, planRoot: _planRoot));

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"work_item_type\"");
        output.ShouldContain("\"plan_status\"");
        output.ShouldContain("\"has_seeded_children\"");
        output.ShouldContain("\"seed_status\"");
        output.ShouldContain("\"implementation_status\"");
        output.ShouldContain("\"workspace_hint\"");
        output.ShouldContain("\"ado_workspace\"");
        output.ShouldContain("\"intent_conflict\"");
        output.ShouldNotContain("\"WorkItemId\"", Case.Sensitive);
        output.ShouldNotContain("\"PlanStatus\"", Case.Sensitive);
    }

    [Fact]
    public void DiscoverPlan_ExplicitPathExists_ReturnsExplicitOverride()
    {
        var planFile = Path.Combine(_planRoot, "explicit.plan.md");
        File.WriteAllText(planFile, "no metadata, but file exists");

        var (status, source, path) = StateCommands.DiscoverPlan(100, planFile, _planRoot);

        status.ShouldBe("complete");
        source.ShouldBe("explicit_override");
        path.ShouldEndWith("explicit.plan.md");
    }

    [Fact]
    public void DiscoverPlan_LegacyTableMetadata_Matches()
    {
        var planFile = Path.Combine(_planRoot, "legacy.plan.md");
        File.WriteAllText(planFile, "# Plan\n\n| **Work Item** | #100 |\n");

        var (status, source, path) = StateCommands.DiscoverPlan(100, "", _planRoot);

        status.ShouldBe("complete");
        source.ShouldBe("filesystem_fallback");
        path.ShouldEndWith("legacy.plan.md");
    }
}
