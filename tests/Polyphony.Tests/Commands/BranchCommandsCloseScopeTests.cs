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

public sealed class BranchCommandsCloseScopeTests : CommandTestBase
{
    private (BranchCommands Command, FakeProcessRunner Runner) CreateCommand(ProcessConfig? config = null)
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var cfg = config ?? Config;
        var walker = new HierarchyWalker(cfg, Repository);
        var validator = new TransitionValidator(cfg);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new BranchCommands(twig, walker, Repository, validator, git, gh, cfg), runner);
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

    private static void ExpectSetActiveAndState(FakeProcessRunner runner, int id, string state)
    {
        runner.WhenExact("twig", ["set", id.ToString(), "--output", "json"],
            new ProcessResult(0, "{}", ""));
        runner.WhenExact("twig", ["state", state, "--output", "json"],
            new ProcessResult(0, "{}", ""));
    }

    [Fact]
    public async Task CloseScope_NoPgIdentifier_EmitsError()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CloseScope(workItem: 100));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--pg-name");
    }

    [Fact]
    public async Task CloseScope_PgNumberDerivesPgName()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        // Root with no children — empty PG, but pg_name should be "PG-3".
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithState("Doing").Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.CloseScope(workItem: 100, pgNumber: 3, prNumber: 42));
        var result = Deserialize(output);

        result.MergeGroupName.ShouldBe("PG-3");
        result.PrNumber.ShouldBe(42);
        result.AdoWorkspace.ShouldBe("org/proj");
    }

    [Fact]
    public async Task CloseScope_PgItemsTransitioned_RecordedInClosedItems()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        ExpectSetActiveAndState(runner, 200, "Done");
        ExpectSetActiveAndState(runner, 201, "Done");

        // Epic root with two PG-1 children in Doing.
        var root = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var child1 = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T1")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        var child2 = new WorkItemBuilder().WithId(201).WithType("Task").WithTitle("T2")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        await SeedAsync(root.Build(), child1.Build(), child2.Build());

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CloseScope(workItem: 100, pgName: "PG-1", prNumber: 7));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.MergeGroupName.ShouldBe("PG-1");
        result.PrNumber.ShouldBe(7);
        result.TotalClosed.ShouldBe(2);
        result.TotalFailed.ShouldBe(0);
        result.ClosedItems.ShouldContain(c => c.Id == 200 && c.TargetState == "Done");
        result.ClosedItems.ShouldContain(c => c.Id == 201 && c.TargetState == "Done");
    }

    [Fact]
    public async Task CloseScope_AlreadyTerminalItems_SkippedNotClosed()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");

        var root = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        // Already Done — must NOT be re-transitioned.
        var child = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T1")
            .WithState("Done").WithTags("PG-1").WithParentId(100);
        await SeedAsync(root.Build(), child.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CloseScope(workItem: 100, pgName: "PG-1"));

        var result = Deserialize(output);
        result.TotalClosed.ShouldBe(0);
        result.TotalFailed.ShouldBe(0);
        // Confirm we never invoked twig set/state for this item.
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "set" && i.Arguments[1] == "200");
    }

    [Fact]
    public async Task CloseScope_OnlyMatchesNamedPg()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        ExpectSetActiveAndState(runner, 200, "Done");

        var root = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var inPg1 = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T1")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        var inPg2 = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T2")
            .WithState("Doing").WithTags("PG-2").WithParentId(100);
        await SeedAsync(root.Build(), inPg1.Build(), inPg2.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CloseScope(workItem: 100, pgName: "PG-1"));
        var result = Deserialize(output);

        result.TotalClosed.ShouldBe(1);
        result.ClosedItems[0].Id.ShouldBe(200);
        // PG-2 item must not have been touched.
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "set" && i.Arguments[1] == "300");
    }

    [Fact]
    public async Task CloseScope_TwigStateFails_RecordedAsFailedClosure()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        runner.WhenExact("twig", ["set", "200", "--output", "json"],
            new ProcessResult(0, "{}", ""));
        runner.WhenExact("twig", ["state", "Done", "--output", "json"],
            new ProcessResult(1, "", "ado rejected transition"));

        var root = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var child = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T1")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        await SeedAsync(root.Build(), child.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CloseScope(workItem: 100, pgName: "PG-1"));
        var result = Deserialize(output);

        result.TotalClosed.ShouldBe(0);
        result.TotalFailed.ShouldBe(1);
        result.FailedClosures[0].Id.ShouldBe(200);
        result.FailedClosures[0].Reason.ShouldContain("Transition failed");
    }

    [Fact]
    public async Task CloseScope_RootNotInCache_EmitsErrorButReturnsSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CloseScope(workItem: 9999, pgName: "PG-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("9999");
    }

    [Fact]
    public async Task CloseScope_OutputIsSnakeCaseJson()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithState("Doing").Build());

        var (_, output) = await CaptureConsoleAsync(() => cmd.CloseScope(workItem: 100, pgName: "PG-1"));

        output.ShouldContain("\"pg_name\"");
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"closed_items\"");
        output.ShouldContain("\"failed_closures\"");
        output.ShouldContain("\"total_closed\"");
        output.ShouldContain("\"total_failed\"");
        output.ShouldContain("\"ado_workspace\"");
        output.ShouldNotContain("\"PgName\"");
        output.ShouldNotContain("\"ClosedItems\"");
    }

    [Fact]
    public async Task CloseScope_BatchedTransitions_FlushesOnceAfterLoop()
    {
        // AB#3126 (sibling-bug to next-impl): close-scope must call
        // `twig sync` once after all transitions to flush the staged
        // terminal-state changes to ADO. Without this push the cluster
        // of closures is invisible to subsequent readers.
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");
        ExpectSetActiveAndState(runner, 200, "Done");
        ExpectSetActiveAndState(runner, 201, "Done");

        var root = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var c1 = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("T1")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        var c2 = new WorkItemBuilder().WithId(201).WithType("Task").WithTitle("T2")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        await SeedAsync(root.Build(), c1.Build(), c2.Build());

        await CaptureConsoleAsync(() => cmd.CloseScope(workItem: 100, pgName: "PG-1"));

        // Find the index of the LAST `twig state Done` invocation; the
        // post-loop flush must come after it.
        var lastStateIdx = -1;
        for (var i = 0; i < runner.Invocations.Count; i++)
        {
            var inv = runner.Invocations[i];
            if (inv.Executable == "twig"
                && inv.Arguments.Count >= 2
                && inv.Arguments[0] == "state"
                && inv.Arguments[1] == "Done")
            {
                lastStateIdx = i;
            }
        }
        lastStateIdx.ShouldBeGreaterThan(-1, "expected at least one twig state Done invocation");

        var postLoopSync = runner.Invocations
            .Select((inv, idx) => (inv, idx))
            .FirstOrDefault(x => x.idx > lastStateIdx
                && x.inv.Executable == "twig"
                && x.inv.Arguments.Count >= 1
                && x.inv.Arguments[0] == "sync");

        postLoopSync.inv.ShouldNotBeNull(
            "close-scope must invoke `twig sync` after the transition loop to flush staged closures (AB#3126)");
    }

    [Fact]
    public async Task CloseScope_NoTransitions_DoesNotFlush()
    {
        // Belt-and-suspenders: when nothing transitioned (all items already
        // terminal), the post-loop flush is skipped — no need to push an
        // empty change set.
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner, "org", "proj");

        var root = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Epic").WithState("Doing");
        var done = new WorkItemBuilder().WithId(200).WithType("Task").WithTitle("Already done")
            .WithState("Done").WithTags("PG-1").WithParentId(100);
        await SeedAsync(root.Build(), done.Build());

        await CaptureConsoleAsync(() => cmd.CloseScope(workItem: 100, pgName: "PG-1"));

        // Exactly one `twig sync` (the start-of-verb sync). No second sync
        // because the loop made no transitions.
        var syncCount = runner.Invocations.Count(i =>
            i.Executable == "twig" && i.Arguments.Count >= 1 && i.Arguments[0] == "sync");
        syncCount.ShouldBe(1);
    }

    private static BranchCloseScopeResult Deserialize(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchCloseScopeResult)
            ?? throw new InvalidOperationException("Failed to deserialize close-scope output");
}
