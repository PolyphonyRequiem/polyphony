using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Services;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class BranchCommandsRouteTests : CommandTestBase
{
    private (BranchCommands Command, FakeProcessRunner Runner) CreateCommand(ProcessConfig? cfg = null)
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var c = cfg ?? Config;
        var walker = new HierarchyWalker(c, Repository);
        var validator = new TransitionValidator(c);
        var resolver = new Polyphony.Sdlc.Observers.RepoIdentityResolver(git);
        var reader = new Polyphony.Sdlc.Observers.PullRequestReader(gh, null);
        return (new BranchCommands(twig, walker, Repository, validator, git, c, resolver, reader), runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    private static void StubConfig(FakeProcessRunner runner, string org = "org", string project = "proj")
    {
        runner.WhenExact("twig", ["config", "organization", "--output", "json"],
            new ProcessResult(0, $$"""{"info":"{{org}}"}""", ""));
        runner.WhenExact("twig", ["config", "project", "--output", "json"],
            new ProcessResult(0, $$"""{"info":"{{project}}"}""", ""));
    }

    private static void StubGitOrigin(FakeProcessRunner runner, string url = "https://github.com/acme/widget.git")
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url, ""));

    private static void StubRemoteBranches(FakeProcessRunner runner, params string[] branches)
    {
        var stdout = string.Join("\n", branches.Select(b => $"  origin/{b}"));
        runner.WhenExact("git", ["branch", "-r"], new ProcessResult(0, stdout, ""));
    }

    private static void StubPrList(FakeProcessRunner runner, string state, string json)
        => runner.WhenStartsWith(
            "gh",
            ["pr", "list", "--repo", "acme/widget", "--state", state],
            new ProcessResult(0, json, ""));

    [Fact]
    public async Task Route_WorkItemNotFound_EmitsError()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 999));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Action.ShouldBe("error");
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("999");
    }

    [Fact]
    public async Task Route_NoPg_NoBranchNoMergedPr_EmitsCreateBranchPg1()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner);
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "open", "[]");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("My Epic").WithState("Doing").Build();
        var task = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T")
            .WithState("To Do").WithParentId(100).Build();
        await SeedAsync(epic, task);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100));
        var result = Deserialize(output);

        result.Action.ShouldBe("create_branch", $"Output was: {output}");
        result.CurrentMergeGroup.ShouldBe("PG-1");
        result.ChildIds.ShouldContain(200);
        result.TotalMergeGroups.ShouldBe(1);
    }

    [Fact]
    public async Task Route_OpenPrMatchesBranch_EmitsSubmitPr()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner, "feature/100-mg-1");
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "open", """[{"number":42,"headRefName":"feature/100-mg-1","url":"https://example.com/pr/42","mergedAt":null}]""");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing").Build();
        var task = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T")
            .WithState("Doing").WithTags("PG-1").WithParentId(100).Build();
        await SeedAsync(epic, task);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100));
        var result = Deserialize(output);

        result.Action.ShouldBe("submit_pr", $"Output was: {output}");
        result.PrNumber.ShouldBe(42);
        result.PrUrl.ShouldBe("https://example.com/pr/42");
        result.CurrentMergeGroup.ShouldBe("PG-1");
    }

    [Fact]
    public async Task Route_MergedPrAndItemsTerminal_EmitsAllComplete()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner, "feature/100-mg-1");
        StubPrList(runner, "merged", """[{"number":7,"headRefName":"feature/100-mg-1","url":"https://example.com/pr/7","mergedAt":"2024-01-01T00:00:00Z"}]""");
        StubPrList(runner, "open", "[]");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing").Build();
        var issue = new WorkItemBuilder().WithId(200).WithType("Issue")
            .WithState("Done").WithTags("PG-1").WithParentId(100).Build();
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T")
            .WithState("Done").WithParentId(200).Build();
        await SeedAsync(epic, issue, task);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100));
        var result = Deserialize(output);

        result.Action.ShouldBe("all_complete", $"Output was: {output}");
        result.CompletedMergeGroups.ShouldContain("PG-1");
        result.RemainingMergeGroups.ShouldBeEmpty();
    }

    [Fact]
    public async Task Route_MergedPrButContainerStillProposed_EmitsCreateBranchAsStale()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner, "feature/100-mg-1");
        StubPrList(runner, "merged", """[{"number":7,"headRefName":"feature/100-mg-1","url":"https://example.com/pr/7","mergedAt":"2024-01-01T00:00:00Z"}]""");
        StubPrList(runner, "open", "[]");

        // Container Issue still in "To Do" → category Proposed → stale defense kicks in.
        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing").Build();
        var issue = new WorkItemBuilder().WithId(200).WithType("Issue")
            .WithState("To Do").WithTags("PG-1").WithParentId(100).Build();
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T")
            .WithState("To Do").WithParentId(200).Build();
        await SeedAsync(epic, issue, task);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100));
        var result = Deserialize(output);

        result.Action.ShouldBe("create_branch", $"Output was: {output}");
        result.CurrentMergeGroup.ShouldBe("PG-1");
    }

    [Fact]
    public async Task Route_PgNumberScoping_OverridesFirstNonComplete()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner);
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "open", "[]");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing").Build();
        var t1 = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T1")
            .WithState("To Do").WithTags("PG-1").WithParentId(100).Build();
        var t2 = new WorkItemBuilder().WithId(201).WithType("Task").WithTitle("T2")
            .WithState("To Do").WithTags("PG-2").WithParentId(100).Build();
        await SeedAsync(epic, t1, t2);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100, pgNumber: 2));
        var result = Deserialize(output);

        result.CurrentMergeGroup.ShouldBe("PG-2", $"Output was: {output}");
        result.ChildIds.ShouldContain(201);
        result.TotalMergeGroups.ShouldBe(2);
    }

    [Fact]
    public async Task Route_AllPgsComplete_EmitsAllComplete()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner);
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "open", "[]");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Done").Build();
        var task = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T")
            .WithState("Done").WithTags("PG-1").WithParentId(100).Build();
        await SeedAsync(epic, task);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100));
        var result = Deserialize(output);

        result.Action.ShouldBe("all_complete", $"Output was: {output}");
        result.CurrentMergeGroup.ShouldBe("");
        result.CompletedMergeGroups.ShouldContain("PG-1");
    }

    [Fact]
    public async Task Route_OutputIsSnakeCaseJson()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner);
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "open", "[]");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing").Build();
        var task = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T")
            .WithState("To Do").WithTags("PG-1").WithParentId(100).Build();
        await SeedAsync(epic, task);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100));

        output.ShouldContain("\"action\"");
        output.ShouldContain("\"current_pg\"");
        output.ShouldContain("\"branch_name\"");
        output.ShouldContain("\"work_item_ids\"");
        output.ShouldContain("\"child_ids\"");
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"completed_pgs\"");
        output.ShouldContain("\"remaining_pgs\"");
        output.ShouldContain("\"total_pgs\"");
        output.ShouldContain("\"ado_workspace\"");
        output.ShouldNotContain("\"CurrentPg\"");
        output.ShouldNotContain("\"BranchName\"");
        output.ShouldNotContain("\"PrNumber\"");
    }

    // F10: indivisible-apex routing — when the hierarchy has no MG tags
    // (BuildRouteGroups synthesizes a single fallback PG-1) and the caller
    // passes --pg-number = apex_id (e.g. 3064), Route used to silently emit
    // action=all_complete because pgNumber matching failed. This is the
    // false-satisfied bug: the apex carries non-terminal items but Route
    // returned "nothing left to do". Now Route accepts the lone synthesized
    // fallback and routes to create_branch instead.
    [Fact]
    public async Task Route_PgNumberMismatchButFallbackOnly_RoutesToFallback()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner);
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "open", "[]");

        // Indivisible apex: a single Epic in non-terminal state, no children,
        // and no PG tags anywhere. BuildRouteGroups will synthesize PG-1 with
        // the apex as a container WorkItem.
        var apex = new WorkItemBuilder()
            .WithId(3064)
            .WithType("Epic")
            .WithTitle("Wire item_satisfied as ADO transition trigger")
            .WithState("To Do")
            .Build();
        await SeedAsync(apex);

        // Caller passes --pg-number = apex_id (the workflow YAML's
        // current shape). Without the F10 fix this returns all_complete.
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 3064, pgNumber: 3064));
        var result = Deserialize(output);

        result.Action.ShouldBe("create_branch", $"Output was: {output}");
        result.CurrentMergeGroup.ShouldBe("PG-1");
        result.TotalMergeGroups.ShouldBe(1);
        result.WorkItemIds.ShouldContain(3064);
    }

    // F10 negative case: when MG tags DO exist and the caller asks for an MG
    // number that none of them carry, the fallback acceptance must NOT kick
    // in (parallel-dispatch correctness). Without an explicit guard a future
    // refactor could reintroduce the wrong-MG bug in this shape.
    [Fact]
    public async Task Route_PgNumberMismatchWithTaggedGroups_DoesNotAcceptFallback()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner);
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "open", "[]");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing").Build();
        var task = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T")
            .WithState("To Do").WithParentId(100).WithTags("PG-1").Build();
        await SeedAsync(epic, task);

        // Caller asks for PG-2 but only PG-1 is tagged. Must NOT silently
        // route to PG-1 — that would break parallel-dispatch invariants.
        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100, pgNumber: 2));
        var result = Deserialize(output);

        result.Action.ShouldBe("all_complete", $"Output was: {output}");
        result.CurrentMergeGroup.ShouldBe("");
    }

    private static BranchRouteResult Deserialize(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchRouteResult)
            ?? throw new InvalidOperationException("Failed to deserialize route output");
}
