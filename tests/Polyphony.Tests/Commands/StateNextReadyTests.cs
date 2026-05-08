using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Sdlc.Observers;
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

    private StateCommands CreateCommand(
        ProcessConfig? configOverride = null,
        Action<FakeProcessRunner>? runnerSetup = null)
    {
        var config = configOverride ?? Config;
        var runner = new FakeProcessRunner();
        // Per-test stubs run FIRST so they win FakeProcessRunner's first-match
        // dispatch over the catch-all baseline. Tests that need to exercise
        // specific impl-PR or plan-PR observations (the only signals the verb
        // currently observes) pass a runnerSetup callback; everything else
        // falls through to "no signal" and the disposition degrades to Needed.
        runnerSetup?.Invoke(runner);
        StubBaselineNoSignal(runner);
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var planObserver = new PlanObserver(git, gh, twig);
        return new StateCommands(twig, git, gh, runner, Repository, config, planObserver);
    }

    private static void StubBaselineNoSignal(FakeProcessRunner runner)
    {
        // git remote get-url origin → empty stdout; PlanObserver returns "" slug.
        runner.WhenStartsWith("git", ["remote", "get-url"], new ProcessResult(0, "", ""));
        // git ls-remote → empty stdout; PlanObserver returns false.
        runner.WhenStartsWith("git", ["ls-remote"], new ProcessResult(0, "", ""));
        // gh pr list → no PRs (catch-all; per-test impl/plan PR stubs win
        // because they're registered earlier via runnerSetup).
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
        // twig show → empty payload (legacy children_seeded path doesn't call this; harmless safety net).
        runner.WhenStartsWith("twig", ["show"], new ProcessResult(0, """{"id":0,"tags":""}""", ""));
    }

    /// <summary>Stub <c>git remote get-url origin</c> with a real-ish URL,
    /// then <c>gh pr list --head impl/{itemId}-{itemId}</c> and the matching
    /// <c>gh pr view</c> with a single impl PR in the requested state.
    /// Caller-provided to <see cref="CreateCommand"/>'s runnerSetup callback
    /// so it wins the first-match dispatch over the catch-all baseline.</summary>
    private static void StubImplPr(FakeProcessRunner runner, int itemId, int prNumber, string state)
    {
        var implBranch = $"impl/{itemId}-{itemId}";
        // Origin URL must resolve to a non-empty slug so FetchImplPrAsync
        // actually queries gh instead of short-circuiting on missing slug.
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, "https://github.com/acme/repo.git\n", ""));
        runner.When(
            (e, a) => e == "gh"
                && a.Count >= 4 && a[0] == "pr" && a[1] == "list"
                && HasHeadFilter(a, implBranch),
            new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"headRefName":"{{implBranch}}","url":"https://gh/pr/{{prNumber}}"}]""",
                ""));
        var poll = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "",
              "mergeable": "MERGEABLE",
              "headRefName": "{{implBranch}}",
              "headRefOid": "abc123",
              "baseRefName": "feature/{{itemId}}",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, poll, ""));
    }

    private static bool HasHeadFilter(IReadOnlyList<string> args, string branch)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--head" && args[i + 1] == branch) return true;
        }
        return false;
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
        // Task type has [implementable] only. Closed-loop PR #4: status=satisfied
        // requires an actually-merged impl PR — item.State="Done" alone no longer
        // suffices (legacy heuristic was removed in favour of canonical
        // gh-PR observation per closed-loop §3.1 row 5).
        var item = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("Done").WithState("Done").Build();
        await SeedAsync(item);

        var cmd = CreateCommand(runnerSetup: r => StubImplPr(r, itemId: 300, prNumber: 700, state: "MERGED"));
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
        // Closed-loop PR #4: status=monitoring requires an open impl PR
        // (Fulfilling). Pre-PR-#4 the verb derived this from item.State="Doing"
        // alone via the heuristic — that heuristic is now removed.
        var item = new WorkItemBuilder().WithId(400).WithType("Task").WithTitle("In flight").WithState("Doing").Build();
        await SeedAsync(item);

        var cmd = CreateCommand(runnerSetup: r => StubImplPr(r, itemId: 400, prNumber: 800, state: "OPEN"));
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
