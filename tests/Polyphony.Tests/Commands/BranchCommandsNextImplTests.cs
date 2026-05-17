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

public sealed class BranchCommandsNextImplTests : CommandTestBase
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
        return (new BranchCommands(twig, walker, Repository, validator, git, c, new Polyphony.Sdlc.Observers.RepoIdentityResolver(git), new Polyphony.Sdlc.Observers.PullRequestReader(gh, null)), runner);
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

    private static void StubBranch(FakeProcessRunner runner, string current)
        => runner.WhenExact("git", ["branch", "--show-current"],
            new ProcessResult(0, current, ""));

    private void ExpectStateTransition(FakeProcessRunner runner, int id, string state)
    {
        runner.WhenExact("twig", ["set", id.ToString(), "--output", "json"],
            new ProcessResult(0, "{}", ""));
        // Mirror twig state's behavior: after pushing the transition to ADO,
        // twig refetches the work item and saves it back to cache. Tests
        // need this so the polyphony read-after-write assertion (AB#3189)
        // sees a consistent state. Without the cache update here, every
        // happy-path test would trip the new mismatch guard.
        runner.WhenAsync(
            (e, a) => e == "twig" && a.Count >= 2 && a[0] == "state" && a[1] == state,
            async (_, _) =>
            {
                var existing = await Repository.GetByIdAsync(id);
                if (existing is not null)
                {
                    existing.ChangeState(state);
                    existing.MarkSynced(existing.Revision + 1);
                    await Repository.SaveAsync(existing);
                }
                return new ProcessResult(0, "{}", "");
            });
    }

    [Fact]
    public async Task NextImpl_NoPgIdentifier_EmitsError()
    {
        var (cmd, _) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextImpl(workItem: 100));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Action.ShouldBe("error");
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--pg-name");
    }

    [Fact]
    public async Task NextImpl_HappyPath_TransitionsFirstNonDoneTask()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        ExpectStateTransition(runner, 300, "Doing");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("My Epic").WithState("Doing");
        var issue = new WorkItemBuilder().WithId(200).WithType("Issue").WithTitle("Issue 1")
            .WithState("Doing").WithParentId(100);
        var t1 = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("First Task")
            .WithState("To Do").WithTags("PG-1").WithParentId(200);
        var t2 = new WorkItemBuilder().WithId(301).WithType("Task").WithTitle("Second Task")
            .WithState("To Do").WithTags("PG-1").WithParentId(200);
        await SeedAsync(epic.Build(), issue.Build(), t1.Build(), t2.Build());

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgName: "PG-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Action.ShouldBe("implement_item");
        result.PrimaryId.ShouldBe(300);
        result.PrimaryTitle.ShouldBe("First Task");
        result.PrimaryType.ShouldBe("Task");
        result.ContainerId.ShouldBe(200);
        result.ContainerTitle.ShouldBe("Issue 1");
        result.ContainerType.ShouldBe("Issue");
        result.RemainingCount.ShouldBe(2);
        result.CurrentMergeGroup.ShouldBe("PG-1");
        result.AdoWorkspace.ShouldBe("org/proj");
        result.BranchName.ShouldStartWith("feature/100-");
    }

    [Fact]
    public async Task NextImpl_AllDone_EmitsAllTasksDone()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithState("Doing");
        var issue = new WorkItemBuilder().WithId(200).WithType("Issue")
            .WithState("Doing").WithParentId(100);
        var done = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("Done")
            .WithState("Done").WithTags("PG-1").WithParentId(200);
        await SeedAsync(epic.Build(), issue.Build(), done.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgName: "PG-1"));
        var result = Deserialize(output);

        result.Action.ShouldBe("all_items_done", $"Output was: {output}");
        result.PrimaryId.ShouldBe(0);
        result.RemainingCount.ShouldBe(0);
        result.BranchName.ShouldBe("");
    }

    [Fact]
    public async Task NextImpl_PgNumberDerivesPgName()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        ExpectStateTransition(runner, 300, "Doing");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing");
        var issue = new WorkItemBuilder().WithId(200).WithType("Issue")
            .WithState("Doing").WithParentId(100);
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T")
            .WithState("To Do").WithTags("PG-3").WithParentId(200);
        await SeedAsync(epic.Build(), issue.Build(), task.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgNumber: 3));
        var result = Deserialize(output);

        result.CurrentMergeGroup.ShouldBe("PG-3");
        result.PrimaryId.ShouldBe(300);
    }

    [Fact]
    public async Task NextImpl_ContainerTaggedNotTask_PicksImplementableUnderContainer()
    {
        // Use an Issue config that is plannable-only (container) so the
        // primary tag-filter doesn't pick it as a task. This forces the
        // fallback "find tasks under PG-tagged container" branch.
        var cfg = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"], new Dictionary<string, string>())
            .WithType("Issue", ["plannable"], new Dictionary<string, string>())
            .WithType("Task", ["implementable"], new Dictionary<string, string>
            {
                ["begin_implementation"] = "Doing",
            })
            .WithBranchStrategy()
            .Build();
        var (cmd, runner) = CreateCommand(cfg);
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        ExpectStateTransition(runner, 300, "Doing");

        // Issue is tagged PG-1; tasks are NOT tagged. Fallback ladder kicks in.
        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithState("Doing");
        var issue = new WorkItemBuilder().WithId(200).WithType("Issue").WithTitle("I1")
            .WithState("Doing").WithTags("PG-1").WithParentId(100);
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T1")
            .WithState("To Do").WithParentId(200);
        await SeedAsync(epic.Build(), issue.Build(), task.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgName: "PG-1"));
        var result = Deserialize(output);

        result.Action.ShouldBe("implement_item", $"Output was: {output}");
        result.PrimaryId.ShouldBe(300);
        result.ContainerId.ShouldBe(200);
    }

    [Fact]
    public async Task NextImpl_BranchHintFromConfig_UsedWhenAvailable()
    {
        // Set a branch strategy that produces feature/{id}-pg-{n}
        var cfg = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"], new Dictionary<string, string>())
            .WithType("Task", ["implementable"], new Dictionary<string, string>
            {
                ["begin_implementation"] = "Doing",
            })
            .WithBranchStrategy(featureBranch: "feature/{id}", MergeGroupBranch: "feature/{id}-pg-{n}")
            .Build();
        var (cmd, runner) = CreateCommand(cfg);
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        ExpectStateTransition(runner, 300, "Doing");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing");
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T")
            .WithState("To Do").WithTags("PG-2").WithParentId(100);
        await SeedAsync(epic.Build(), task.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgName: "PG-2"));
        var result = Deserialize(output);

        result.BranchName.ShouldBe("feature/100-pg-2");
    }

    [Fact]
    public async Task NextImpl_AlreadyOnExpectedBranch_KeepsCurrent()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "feature/100-mg-1");
        ExpectStateTransition(runner, 300, "Doing");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing");
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T")
            .WithState("To Do").WithTags("PG-1").WithParentId(100);
        await SeedAsync(epic.Build(), task.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgName: "PG-1"));
        var result = Deserialize(output);

        result.BranchName.ShouldBe("feature/100-mg-1");
    }

    [Fact]
    public async Task NextImpl_OutputIsSnakeCaseJson()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithState("Doing").Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgName: "PG-1"));

        output.ShouldContain("\"primary_id\"");
        output.ShouldContain("\"primary_title\"");
        output.ShouldContain("\"primary_type\"");
        output.ShouldContain("\"container_id\"");
        output.ShouldContain("\"container_title\"");
        output.ShouldContain("\"container_type\"");
        output.ShouldContain("\"remaining_count\"");
        output.ShouldContain("\"current_pg\"");
        output.ShouldContain("\"branch_name\"");
        output.ShouldContain("\"ado_workspace\"");
        output.ShouldNotContain("\"TaskId\"");
        output.ShouldNotContain("\"BranchName\"");
    }

    [Fact]
    public async Task NextImpl_HappyPath_FlushesStagedTransitionAfterSetState()
    {
        // AB#3126 regression: `next-impl` must call `twig sync` immediately
        // after `twig state` so the staged begin_implementation transition
        // is durable in ADO before the verb returns. Otherwise the staged
        // change is invisible to the next reader (e.g. `polyphony validate`
        // in `primary_completer`), which reads cache directly without
        // syncing first.
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        ExpectStateTransition(runner, 300, "Doing");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing");
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T")
            .WithState("To Do").WithTags("PG-1").WithParentId(100);
        await SeedAsync(epic.Build(), task.Build());

        await CaptureConsoleAsync(() => cmd.NextImpl(workItem: 100, pgName: "PG-1"));

        // Find the index of the post-state `twig sync` invocation: it must
        // come AFTER the `twig state Doing` call. The verb also calls
        // `twig sync` at the start, so we need to find a sync call whose
        // index is greater than the state call's index.
        var stateIdx = runner.Invocations.ToList().FindIndex(
            i => i.Executable == "twig"
                && i.Arguments.Count >= 2
                && i.Arguments[0] == "state"
                && i.Arguments[1] == "Doing");
        stateIdx.ShouldBeGreaterThan(-1, "twig state Doing must have been invoked");

        var postStateSync = runner.Invocations
            .Select((inv, idx) => (inv, idx))
            .FirstOrDefault(x => x.idx > stateIdx
                && x.inv.Executable == "twig"
                && x.inv.Arguments.Count >= 1
                && x.inv.Arguments[0] == "sync");

        postStateSync.inv.ShouldNotBeNull(
            "next-impl must invoke `twig sync` after `twig state` to flush the staged transition (AB#3126)");
    }

    [Fact]
    public async Task NextImpl_PostSyncStateMismatch_EmitsErrorWithDiagnostics()
    {
        // AB#3189 regression: when `twig state` + `twig sync` both exit 0
        // but the cache still reports the pre-transition state (apex 3165
        // dispatch_items[0] for AB#3172 — observed 12-minute self-heal
        // before second next-impl invocation finally saw Doing), the verb
        // must surface action=error with the task id, transition, and ADO
        // URL instead of returning success and leaving primary_completer
        // to refuse implementation_complete with no diagnostic.
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var validator = new TransitionValidator(Config);
        var cmd = new BranchCommands(twig, walker, Repository, validator, git, Config, new Polyphony.Sdlc.Observers.RepoIdentityResolver(git), new Polyphony.Sdlc.Observers.PullRequestReader(gh, null));

        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        // SetActive succeeds; SetState succeeds at the boundary BUT we
        // intentionally do NOT update the cache here, simulating the race
        // where twig sync's pull-back overwrites the freshly-pushed state
        // before ADO has settled.
        runner.WhenExact("twig", ["set", "300", "--output", "json"], new ProcessResult(0, "{}", ""));
        runner.WhenExact("twig", ["state", "Doing", "--output", "json"], new ProcessResult(0, "{}", ""));

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing");
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T")
            .WithState("To Do").WithTags("PG-1").WithParentId(100);
        await SeedAsync(epic.Build(), task.Build());

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgName: "PG-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Action.ShouldBe("error", $"Output was: {output}");
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("#300");
        result.Error.ShouldContain("Doing");
        result.Error.ShouldContain("To Do");
        result.Error.ShouldContain("https://dev.azure.com/org/proj/_workitems/edit/300");
    }

    [Fact]
    public async Task NextImpl_TwigStateThrows_ErrorEnvelopeIncludesAdoUrl()
    {
        // AB#3191 regression: when the twig boundary fails, the error
        // envelope must include enough context (root work item id, merge
        // group, and ADO URL) for the operator to jump straight to the
        // work item. Pre-AB#3191 the message was just "Error routing next
        // task: <ex.Message>" — no task id, no URL.
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        runner.WhenExact("twig", ["set", "300", "--output", "json"], new ProcessResult(0, "{}", ""));
        runner.WhenExact("twig", ["state", "Doing", "--output", "json"],
            new ProcessResult(1, "", "ado unreachable"));

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("E").WithState("Doing");
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("T")
            .WithState("To Do").WithTags("PG-1").WithParentId(100);
        await SeedAsync(epic.Build(), task.Build());

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgName: "PG-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Action.ShouldBe("error", $"Output was: {output}");
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("PG-1");
        result.Error.ShouldContain("#100");
        result.Error.ShouldContain("begin_implementation");
        result.Error.ShouldContain("https://dev.azure.com/org/proj/_workitems/edit/100");
    }

    [Fact]
    public async Task NextImpl_ApexRootTaggedImplMergedInMg_ReportsAllItemsDone()
    {
        // AB#3217 regression: when primary_completer has stamped the
        // impl-merged-in-mg=<mg-path> marker on the apex root (because the
        // apex root's terminal transition is deferred to
        // close_mark_satisfied per AB#3169), next-impl MUST filter that
        // item out and report all_items_done, not re-dispatch the same
        // apex root for another empty squash that fails the coverage
        // assertion. Apex 62286666 dogfood: same item came back from
        // next-impl three times before the user killed the loop.
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);

        var apexRoot = new WorkItemBuilder().WithId(100).WithType("Task").WithTitle("Apex")
            .WithState("Doing").WithTags("polyphony:root; PG-1; polyphony:impl-merged-in-mg=pg-1");
        await SeedAsync(apexRoot.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgNumber: 1, mgPath: "pg-1"));
        var result = Deserialize(output);

        result.Action.ShouldBe("all_items_done", $"Output was: {output}");
        result.PrimaryId.ShouldBe(0);
        result.RemainingCount.ShouldBe(0);
    }

    [Fact]
    public async Task NextImpl_ApexRootTaggedForDifferentMg_StillDispatches()
    {
        // Multi-MG hygiene: the marker is per-MG. An apex root that has
        // completed its impl in pg-1 can still be the next implementable
        // for pg-2 (e.g. apex root participates in two parallel MGs).
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        ExpectStateTransition(runner, 100, "Doing");

        var apexRoot = new WorkItemBuilder().WithId(100).WithType("Task").WithTitle("Apex")
            .WithState("To Do").WithTags("polyphony:root; PG-2; polyphony:impl-merged-in-mg=pg-1");
        await SeedAsync(apexRoot.Build());

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.NextImpl(workItem: 100, pgNumber: 2, mgPath: "pg-2"));
        var result = Deserialize(output);

        result.Action.ShouldBe("implement_item", $"Output was: {output}");
        result.PrimaryId.ShouldBe(100);
    }

    [Fact]
    public async Task NextImpl_MgPathCasingDiffersFromTag_StillFilters()
    {
        // Rev 4 mg_path is lower-case by grammar (`^[a-z][a-z0-9-]{0,30}$`),
        // but pg-name flows in upper-case as PG-N from legacy callers.
        // The marker normalizer lowercases both writer and reader so a
        // mixed-case call still matches a lower-case stamped tag.
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);

        var apexRoot = new WorkItemBuilder().WithId(100).WithType("Task").WithTitle("Apex")
            .WithState("Doing").WithTags("polyphony:root; PG-1; polyphony:impl-merged-in-mg=pg-1");
        await SeedAsync(apexRoot.Build());

        var (_, output) = await CaptureConsoleAsync(
            // Caller passes PG-1 (upper) — must still match the stamped pg-1 (lower).
            () => cmd.NextImpl(workItem: 100, pgNumber: 1, mgPath: "PG-1"));
        var result = Deserialize(output);

        result.Action.ShouldBe("all_items_done", $"Output was: {output}");
    }

    private static BranchNextImplResult Deserialize(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchNextImplResult)
            ?? throw new InvalidOperationException("Failed to deserialize next-impl output");
}
