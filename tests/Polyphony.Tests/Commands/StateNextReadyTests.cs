using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony state next-ready</c>. Verifies the verb's
/// status classification, error contract, and the per-disposition payload arrays
/// the driver routes on.
/// </summary>
public sealed class StateNextReadyTests : CommandTestBase
{
    private readonly string _planRoot;

    public StateNextReadyTests()
    {
        _planRoot = Path.Combine(Path.GetTempPath(), "polyphony-next-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planRoot);
    }

    private StateCommands CreateCommand(ProcessConfig? configOverride = null)
    {
        var config = configOverride ?? Config;
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return new StateCommands(twig, git, gh, runner, Repository, config);
    }

    [Fact]
    public async Task NextReady_WorkItemNotFound_ReturnsCacheError_WithCanonicalErrorJson()
    {
        var cmd = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: 9999, planRoot: _planRoot));

        exit.ShouldBe(ExitCodes.CacheError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString()!.ShouldContain("9999");
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(9999);
    }

    [Fact]
    public async Task NextReady_TypeNotInProcessConfig_ReturnsConfigError()
    {
        // Build an item whose type is not in the default config.
        var item = new WorkItemBuilder().WithId(100).WithType("Bug").WithTitle("Unknown").WithState("To Do").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: 100, planRoot: _planRoot));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldBe("error");
        result.Error!.ShouldContain("Bug");
    }

    [Fact]
    public async Task NextReady_PlannableImplementableNoPlan_StatusDispatchable_NextIsPlanAuthored()
    {
        // Issue type in the default config has [plannable, implementable] facets.
        // No plan file exists → plan_authored is the only Ready requirement.
        var item = new WorkItemBuilder().WithId(200).WithType("Issue").WithTitle("Test").WithState("To Do").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: 200, planRoot: _planRoot));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldBe("dispatchable");
        result.Next.ShouldContain(RequirementKind.PlanAuthored);
        result.Needed.ShouldContain(RequirementKind.PlanReviewed);
        result.Needed.ShouldContain(RequirementKind.PlanPromoted);
    }

    [Fact]
    public async Task NextReady_TaskTypeImplementableOnly_DoneItem_StatusSatisfied()
    {
        // Task type has [implementable] only and item is Done → implementation_merged
        // observed Satisfied → status=satisfied.
        var item = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("Done").WithState("Done").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: 300, planRoot: _planRoot));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldBe("satisfied");
        result.Satisfied.ShouldContain(RequirementKind.ImplementationMerged);
        result.Next.ShouldBeEmpty();
    }

    [Fact]
    public async Task NextReady_ImplementableInProgress_StatusMonitoring()
    {
        var item = new WorkItemBuilder().WithId(400).WithType("Task").WithTitle("In flight").WithState("Doing").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: 400, planRoot: _planRoot));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldBe("monitoring");
        result.Fulfilling.ShouldContain(RequirementKind.ImplementationMerged);
    }

    [Fact]
    public async Task NextReady_PureContainer_EmptyFacetsDecomposable_StatusEmpty()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Container", facets: [], allowedChildTypes: ["Task"])
            .Build();
        // Mark the type explicitly decomposable so the deriver permits empty facets.
        config.Types["Container"].Decomposable = true;

        var item = new WorkItemBuilder().WithId(500).WithType("Container").WithTitle("Org").WithState("To Do").Build();
        await SeedAsync(item);

        var cmd = CreateCommand(config);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: 500, planRoot: _planRoot));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // Status is still "empty" — no own-work to drive forward — but the
        // synthetic terminal is now visible in the requirement set, awaiting
        // cross-item rollup from children (out of scope for PR #1).
        result.Status.ShouldBe("empty");
        result.Requirements.Count.ShouldBe(1);
        result.Requirements[0].Kind.ShouldBe(RequirementKind.ItemSatisfied);
        result.Requirements[0].Disposition.ShouldBe(Disposition.Needed);
        result.Next.ShouldBeEmpty();
    }

    [Fact]
    public async Task NextReady_OutputIsSnakeCase()
    {
        var item = new WorkItemBuilder().WithId(600).WithType("Task").WithTitle("Snake").WithState("To Do").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: 600, planRoot: _planRoot));

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"work_item_type\"");
        output.ShouldContain("\"status\"");
        output.ShouldContain("\"requirements\"");
        output.ShouldContain("\"next\"");
        output.ShouldContain("\"resolved_inputs\"");
        output.ShouldNotContain("\"WorkItemId\"");
        output.ShouldNotContain("\"WorkItemType\"");
    }
}
