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
        return (new BranchCommands(twig, walker, Repository, validator, git, gh, c), runner);
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
        result.CurrentPg.ShouldBe("PG-1");
        result.ChildIds.ShouldContain(200);
        result.TotalPgs.ShouldBe(1);
    }

    [Fact]
    public async Task Route_OpenPrMatchesBranch_EmitsSubmitPr()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner, "feature/100-pg-1");
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "open", """[{"number":42,"headRefName":"feature/100-pg-1","url":"https://example.com/pr/42","mergedAt":null}]""");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing").Build();
        var task = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T")
            .WithState("Doing").WithTags("PG-1").WithParentId(100).Build();
        await SeedAsync(epic, task);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Route(workItem: 100));
        var result = Deserialize(output);

        result.Action.ShouldBe("submit_pr", $"Output was: {output}");
        result.PrNumber.ShouldBe(42);
        result.PrUrl.ShouldBe("https://example.com/pr/42");
        result.CurrentPg.ShouldBe("PG-1");
    }

    [Fact]
    public async Task Route_MergedPrAndItemsTerminal_EmitsAllComplete()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner, "feature/100-pg-1");
        StubPrList(runner, "merged", """[{"number":7,"headRefName":"feature/100-pg-1","url":"https://example.com/pr/7","mergedAt":"2024-01-01T00:00:00Z"}]""");
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
        result.CompletedPgs.ShouldContain("PG-1");
        result.RemainingPgs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Route_MergedPrButContainerStillProposed_EmitsCreateBranchAsStale()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubGitOrigin(runner);
        StubRemoteBranches(runner, "feature/100-pg-1");
        StubPrList(runner, "merged", """[{"number":7,"headRefName":"feature/100-pg-1","url":"https://example.com/pr/7","mergedAt":"2024-01-01T00:00:00Z"}]""");
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
        result.CurrentPg.ShouldBe("PG-1");
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

        result.CurrentPg.ShouldBe("PG-2", $"Output was: {output}");
        result.ChildIds.ShouldContain(201);
        result.TotalPgs.ShouldBe(2);
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
        result.CurrentPg.ShouldBe("");
        result.CompletedPgs.ShouldContain("PG-1");
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
        output.ShouldContain("\"issue_ids\"");
        output.ShouldContain("\"task_ids\"");
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

    private static BranchRouteResult Deserialize(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchRouteResult)
            ?? throw new InvalidOperationException("Failed to deserialize route output");
}
