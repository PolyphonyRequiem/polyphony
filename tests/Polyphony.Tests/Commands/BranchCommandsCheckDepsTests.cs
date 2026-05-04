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

public sealed class BranchCommandsCheckDepsTests : CommandTestBase
{
    private static FakeProcessRunner CreateRunner() => new();

    private static (BranchCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        // CheckDeps doesn't touch HierarchyWalker / TransitionValidator / Repository,
        // but the primary ctor still requires them. Pass minimal real instances.
        var config = new ProcessConfigBuilder()
            .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>())
            .Build();
        var store = new SqliteCacheStore("Data Source=:memory:");
        var repo = new SqliteWorkItemRepository(store, new WorkItemMapper());
        var walker = new HierarchyWalker(config, repo);
        var validator = new TransitionValidator(config);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new BranchCommands(twig, walker, repo, validator, git, gh), runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    private static void StubShow(FakeProcessRunner runner, int id, string json, int exitCode = 0)
        => runner.WhenExact("twig", ["show", id.ToString(), "--output", "json"],
            new ProcessResult(exitCode, json, exitCode == 0 ? "" : "not found"));

    [Fact]
    public async Task CheckDeps_NoPredecessors_EmitsNotBlocked()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubShow(runner, 100, """{"id":100,"relations":[]}""");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckDeps(100));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Blocked.ShouldBeFalse();
        result.Status.ShouldBe("not_blocked");
        result.WorkItemId.ShouldBe(100);
        result.BlockingItems.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
        result.Message.ShouldContain("No predecessor");
    }

    [Fact]
    public async Task CheckDeps_AllPredecessorsTerminal_EmitsNotBlocked()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubShow(runner, 100, """
            {
              "id": 100,
              "relations": [
                {"rel":"System.LinkTypes.Dependency-Reverse","url":"https://ado/_apis/wit/workItems/200"},
                {"rel":"System.LinkTypes.Dependency-Reverse","url":"https://ado/_apis/wit/workItems/201"}
              ]
            }
            """);
        StubShow(runner, 200, """{"id":200,"fields":{"System.State":"Done","System.Title":"Pred A"}}""");
        StubShow(runner, 201, """{"id":201,"fields":{"System.State":"Closed","System.Title":"Pred B"}}""");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckDeps(100));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Blocked.ShouldBeFalse();
        result.Status.ShouldBe("not_blocked");
        result.ReadyCount.ShouldBe(2);
        result.TotalCount.ShouldBe(2);
        result.BlockingItems.ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckDeps_PartiallyBlocked_EmitsBlockedListWithPendingItems()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubShow(runner, 100, """
            {
              "id": 100,
              "relations": [
                {"rel":"System.LinkTypes.Dependency-Reverse","url":"https://ado/_apis/wit/workItems/200"},
                {"rel":"System.LinkTypes.Dependency-Reverse","url":"https://ado/_apis/wit/workItems/201"},
                {"rel":"System.LinkTypes.Dependency-Reverse","url":"https://ado/_apis/wit/workItems/202"}
              ]
            }
            """);
        StubShow(runner, 200, """{"id":200,"fields":{"System.State":"Done","System.Title":"Pred A"}}""");
        StubShow(runner, 201, """{"id":201,"fields":{"System.State":"Doing","System.Title":"Pred B"}}""");
        StubShow(runner, 202, """{"id":202,"fields":{"System.State":"To Do","System.Title":"Pred C"}}""");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckDeps(100));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Blocked.ShouldBeTrue();
        result.Status.ShouldBe("blocked");
        result.ReadyCount.ShouldBe(1);
        result.TotalCount.ShouldBe(3);
        result.BlockingItems.Count.ShouldBe(2);
        result.BlockingItems.ShouldContain(b => b.Id == 201 && b.State == "Doing");
        result.BlockingItems.ShouldContain(b => b.Id == 202 && b.State == "To Do");
    }

    [Fact]
    public async Task CheckDeps_PredecessorsByAttributesName_AlsoCounted()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubShow(runner, 100, """
            {
              "id": 100,
              "relations": [
                {"rel":"System.LinkTypes.Hierarchy-Forward","attributes":{"name":"Predecessor"},"url":"https://ado/_apis/wit/workItems/300"}
              ]
            }
            """);
        StubShow(runner, 300, """{"id":300,"fields":{"System.State":"Done","System.Title":"Pred X"}}""");

        var (_, output) = await CaptureConsoleAsync(() => cmd.CheckDeps(100));
        var result = Deserialize(output);

        result.TotalCount.ShouldBe(1);
        result.Blocked.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckDeps_PredecessorFetchFails_RecordsAsUnknown()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubShow(runner, 100, """
            {
              "id": 100,
              "relations": [
                {"rel":"System.LinkTypes.Dependency-Reverse","url":"https://ado/_apis/wit/workItems/999"}
              ]
            }
            """);
        StubShow(runner, 999, "", exitCode: 1);

        var (_, output) = await CaptureConsoleAsync(() => cmd.CheckDeps(100));
        var result = Deserialize(output);

        result.Blocked.ShouldBeTrue();
        result.BlockingItems.Count.ShouldBe(1);
        result.BlockingItems[0].Id.ShouldBe(999);
        result.BlockingItems[0].State.ShouldBe("Unknown");
        result.BlockingItems[0].Title.ShouldContain("Unknown");
    }

    [Fact]
    public async Task CheckDeps_WorkItemNotFound_EmitsErrorResultButReturnsSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubShow(runner, 555, "", exitCode: 1);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckDeps(555));

        // Routing-style verb — always exits 0 even on error; workflow routes on JSON.
        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Blocked.ShouldBeFalse();
        result.Message.ShouldContain("Failed to fetch");
        result.Error.ShouldBe(true);
    }

    [Fact]
    public async Task CheckDeps_SyncFailure_EmitsErrorResult()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("twig", ["sync", "--output", "json"],
            new ProcessResult(1, "", "ado unreachable"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckDeps(100));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Error.ShouldBe(true);
        result.Message.ShouldContain("Error checking dependencies");
    }

    [Fact]
    public async Task CheckDeps_OutputIsSnakeCaseJson()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubShow(runner, 100, """{"id":100,"relations":[]}""");

        var (_, output) = await CaptureConsoleAsync(() => cmd.CheckDeps(100));

        // Field names must be snake_case per the cross-cutting JSON contract.
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"blocking_items\"");
        output.ShouldContain("\"ready_count\"");
        output.ShouldContain("\"total_count\"");
        // Pascal-case names must NOT leak.
        output.ShouldNotContain("\"WorkItemId\"");
        output.ShouldNotContain("\"BlockingItems\"");
    }

    private static BranchCheckDepsResult Deserialize(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchCheckDepsResult)
            ?? throw new InvalidOperationException("Failed to deserialize check-deps output");
}
