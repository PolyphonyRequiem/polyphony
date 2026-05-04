using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Services;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class BranchCommandsLoadTreeTests : CommandTestBase
{
    private (BranchCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twigClient = new TwigClient(runner);
        var gitClient = new GitClient(runner);
        var ghClient = new GhClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var validator = new TransitionValidator(Config);
        return (new BranchCommands(twigClient, walker, Repository, validator, gitClient, ghClient, Config), runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    private static void StubConfig(FakeProcessRunner runner, string org, string project)
    {
        runner.WhenExact("twig", ["config", "organization", "--output", "json"],
            new ProcessResult(0, $$"""{"info":"{{org}}"}""", ""));
        runner.WhenExact("twig", ["config", "project", "--output", "json"],
            new ProcessResult(0, $$"""{"info":"{{project}}"}""", ""));
    }

    private static void StubRemote(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, url, ""));

    private static void StubMergedPrsEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0, "[]", ""));

    private static void StubMergedPrs(FakeProcessRunner runner, params (int Number, string Branch)[] prs)
    {
        var json = "[" + string.Join(",", prs.Select(p =>
            $$"""{"number":{{p.Number}},"headRefName":"{{p.Branch}}","url":"https://gh","mergedAt":"2026-01-01T00:00:00Z"}""")) + "]";
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, json, ""));
    }

    [Fact]
    public async Task LoadTree_RootNotFound_EmitsErrorButReturnsSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "", "");
        StubRemote(runner, "");
        StubMergedPrsEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.LoadTree(9999));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("9999");
    }

    [Fact]
    public async Task LoadTree_NoPgTags_FallsBackToSinglePg1()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        StubRemote(runner, "git@github.com:owner/repo.git");
        StubMergedPrsEmpty(runner);

        // Epic with one Issue child, one Task grandchild — no PG tags.
        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("My Epic").WithState("Doing");
        var issue = new WorkItemBuilder().WithId(200).WithType("Issue").WithTitle("Issue 1")
            .WithState("Doing").WithParentId(100);
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("Task 1")
            .WithState("Doing").WithParentId(200);
        await SeedAsync(epic.Build(), issue.Build(), task.Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadTree(100));
        var result = Deserialize(output);

        result.PrGroups.Count.ShouldBe(1);
        result.PrGroups[0].Name.ShouldBe("PG-1");
        result.PrGroups[0].MergedPr.ShouldBe(0);
        result.PrGroups[0].Completed.ShouldBeFalse();
        result.PendingPgs.ShouldContain("PG-1");
        result.NextPg.ShouldBe("PG-1");
        result.TaggedItems.ShouldBe(0);
        result.UntaggedItems.ShouldBe(3);
    }

    [Fact]
    public async Task LoadTree_PgTags_GroupsItemsByPg()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        StubRemote(runner, "https://github.com/owner/repo.git");
        StubMergedPrsEmpty(runner);

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var i1 = new WorkItemBuilder().WithId(200).WithType("Issue").WithTitle("I1")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        var t1 = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T1")
            .WithState("Doing").WithTags("PG-1").WithParentId(200);
        var i2 = new WorkItemBuilder().WithId(201).WithType("Issue").WithTitle("I2")
            .WithState("Doing").WithTags("PG-2").WithParentId(100);
        var t2 = new WorkItemBuilder().WithId(301).WithType("Task").WithTitle("T2")
            .WithState("Doing").WithTags("PG-2").WithParentId(201);
        await SeedAsync(epic.Build(), i1.Build(), t1.Build(), i2.Build(), t2.Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadTree(100));
        var result = Deserialize(output);

        result.PrGroups.Count.ShouldBe(2);
        // Sorted by N (PG-1 before PG-2).
        result.PrGroups[0].Name.ShouldBe("PG-1");
        result.PrGroups[1].Name.ShouldBe("PG-2");
        result.PrGroups[0].BranchNameSuggestion.ShouldStartWith("feature/pg-1");
        result.NextPg.ShouldBe("PG-1");
        result.TaggedItems.ShouldBe(4);
        result.WorkTree.EpicId.ShouldBe(100);
        result.WorkTree.Issues.Count.ShouldBe(2);
    }

    [Fact]
    public async Task LoadTree_MergedPr_MarksPgCompleted()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        StubRemote(runner, "git@github.com:owner/repo.git");
        StubMergedPrs(runner, (42, "feature/pg-1"));

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T1")
            .WithState("Done").WithTags("PG-1").WithParentId(100);
        await SeedAsync(epic.Build(), task.Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadTree(100));
        var result = Deserialize(output);

        var pg = result.PrGroups.Single();
        pg.MergedPr.ShouldBe(42);
        pg.Completed.ShouldBeTrue();
        result.CompletedPgs.ShouldContain("PG-1");
        result.NextPg.ShouldBe("");
    }

    [Fact]
    public async Task LoadTree_CompletedPgWithStaleDoingTasks_NeedsReconciliation()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        StubRemote(runner, "git@github.com:owner/repo.git");
        StubMergedPrs(runner, (42, "feature/pg-1"));

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var staleTask = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("Stale")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        await SeedAsync(epic.Build(), staleTask.Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadTree(100));
        var result = Deserialize(output);

        var pg = result.PrGroups.Single();
        pg.Completed.ShouldBeTrue();
        pg.NeedsReconciliation.ShouldBeTrue();
        pg.StaleDoingTaskIds.ShouldContain(300);
        result.PgsNeedingReconciliation.Count.ShouldBe(1);
        result.PgsNeedingReconciliation[0].StaleDoingTaskIds.ShouldContain(300);
    }

    [Fact]
    public async Task LoadTree_NoGitHubRemote_PgsRemainPending()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        // git remote get-url returns ADO URL — slug regex fails to match.
        StubRemote(runner, "https://dev.azure.com/foo/bar/_git/repo");
        StubMergedPrsEmpty(runner);

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T1")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        await SeedAsync(epic.Build(), task.Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadTree(100));
        var result = Deserialize(output);

        result.PrGroups.Single().MergedPr.ShouldBe(0);
        result.PrGroups.Single().Completed.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadTree_AdoWorkspace_DerivedFromTwigConfig()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "myorg", "myproj");
        StubRemote(runner, "");
        StubMergedPrsEmpty(runner);
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithState("Doing").Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadTree(100));
        var result = Deserialize(output);

        result.AdoOrg.ShouldBe("myorg");
        result.AdoProject.ShouldBe("myproj");
        result.AdoWorkspace.ShouldBe("myorg/myproj");
    }

    [Fact]
    public async Task LoadTree_OutputIsSnakeCaseJson()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "", "");
        StubRemote(runner, "");
        StubMergedPrsEmpty(runner);
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithState("Doing").Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.LoadTree(100));

        output.ShouldContain("\"work_tree\"");
        output.ShouldContain("\"pr_groups\"");
        output.ShouldContain("\"completed_pgs\"");
        output.ShouldContain("\"pending_pgs\"");
        output.ShouldContain("\"next_pg\"");
        output.ShouldContain("\"pgs_needing_reconciliation\"");
        output.ShouldContain("\"total_tasks\"");
        output.ShouldContain("\"ado_workspace\"");
        output.ShouldNotContain("\"WorkTree\"");
        output.ShouldNotContain("\"PrGroups\"");
    }

    private static BranchLoadTreeResult Deserialize(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchLoadTreeResult)
            ?? throw new InvalidOperationException("Failed to deserialize load-tree output");
}
