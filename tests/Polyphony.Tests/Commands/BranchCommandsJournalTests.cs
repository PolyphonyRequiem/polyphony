using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Services;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class BranchCommandsJournalTests : CommandTestBase
{
    private readonly string _scratchRoot = Path.Combine(AppContext.BaseDirectory, "branch-journal-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _createdDirs = [];

    [Fact]
    public async Task EnsureEvidenceBranch_Created_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: false);
        StubLocalBranchExistsSequence(runner, "evidence/100-200", existsBefore: false, finalSha: "abc123");
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: true, sha: "base123");
        StubCreateBranch(runner, "evidence/100-200", "feature/100");
        StubPush(runner, "evidence/100-200");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.EnsureEvidenceBranch(workItemId: 200, rootId: 100));
        var entries = await store.QueryAsync(new JournalQuery { Action = "branch_ensure_evidence_branch" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("evidence/100-200");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.BranchEnsureEvidenceBranchPayload);
        payload.ShouldNotBeNull();
        payload.RootId.ShouldBe(100);
        payload.WorkItemId.ShouldBe(200);
        payload.BranchName.ShouldBe("evidence/100-200");
        payload.Sha.ShouldBe("abc123");
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.GitBranch, "evidence/100-200", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true);
    }

    [Fact]
    public async Task EnsureFeature_AlreadyOnBranch_WritesNoOpJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemote(runner, "feature/3043", exists: true);
        StubLocalBranchExists(runner, "feature/3043", exists: true, sha: "abc123");
        StubBranch(runner, "feature/3043");
        StubCheckout(runner, "feature/3043");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.EnsureFeature(branch: "feature/3043"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "branch_ensure_feature" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("feature/3043");
        entry.Outcome.ShouldBe(JournalOutcome.NoOp);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.BranchEnsureFeaturePayload);
        payload.ShouldNotBeNull();
        payload.RootId.ShouldBe(3043);
        payload.WasMutated.ShouldBeFalse();
        payload.Sha.ShouldBe("abc123");
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.GitBranch, "feature/3043", ResourceIntent.EnsurePresent, ResourceMutation.NoChangedExternalAlreadyPresent, polyphonyOwned: false);
    }

    [Fact]
    public async Task EnsureImpl_Created_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemote(runner, "impl/100-200", exists: false);
        StubLocalBranchExistsSequence(runner, "impl/100-200", existsBefore: false, finalSha: "impl123");
        StubLsRemote(runner, "mg/100_pg-1", exists: true);
        StubLocalBranchExists(runner, "mg/100_pg-1", exists: true, sha: "base123");
        StubCreateBranch(runner, "impl/100-200", "mg/100_pg-1");
        StubPush(runner, "impl/100-200");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "pg-1"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "branch_ensure_impl" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("impl/100-200");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.BranchEnsureImplPayload);
        payload.ShouldNotBeNull();
        payload.MergeGroupPath.ShouldBe("pg-1");
        payload.Sha.ShouldBe("impl123");
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.GitBranch, "impl/100-200", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true);
    }

    [Fact]
    public async Task EnsureMergeGroup_Created_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemote(runner, "mg/100_pg-1", exists: false);
        StubLocalBranchExistsSequence(runner, "mg/100_pg-1", existsBefore: false, finalSha: "mg123");
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: true, sha: "feature123");
        StubCreateBranch(runner, "mg/100_pg-1", "feature/100");
        StubPush(runner, "mg/100_pg-1");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "pg-1"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "branch_ensure_merge_group" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("mg/100_pg-1");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.BranchEnsureMergeGroupPayload);
        payload.ShouldNotBeNull();
        payload.Depth.ShouldBe(1);
        payload.Sha.ShouldBe("mg123");
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.GitBranch, "mg/100_pg-1", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true);
    }

    [Fact]
    public async Task EnsurePlan_Created_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemote(runner, "plan/100", exists: false);
        StubLocalBranchExistsSequence(runner, "plan/100", existsBefore: false, finalSha: "plan123");
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: true, sha: "feature123");
        StubCreateBranch(runner, "plan/100", "feature/100");
        StubPush(runner, "plan/100");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.EnsurePlan(rootId: 100, itemId: 100));
        var entries = await store.QueryAsync(new JournalQuery { Action = "branch_ensure_plan" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("plan/100");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.BranchEnsurePlanPayload);
        payload.ShouldNotBeNull();
        payload.IsRootPlan.ShouldBeTrue();
        payload.Sha.ShouldBe("plan123");
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.GitBranch, "plan/100", ResourceIntent.EnsurePresent, ResourceMutation.CreatedNow, polyphonyOwned: true);
    }

    [Fact]
    public async Task MarkImplMerged_AlreadyStamped_WritesNoOpJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root; polyphony:impl-merged-in-mg=pg-1");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.MarkImplMerged(workItem: 100, mgPath: "pg-1"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "branch_mark_impl_merged" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("workitem:100");
        entry.Outcome.ShouldBe(JournalOutcome.NoOp);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.BranchMarkImplMergedPayload);
        payload.ShouldNotBeNull();
        payload.AlreadyInDesiredState.ShouldBeTrue();
        payload.WasMutated.ShouldBeFalse();
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.AdoWorkItemTag, "100:polyphony:impl-merged-in-mg=pg-1", ResourceIntent.EnsurePresent, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: true);
    }

    [Fact]
    public async Task ClearImplMerged_RemovesTag_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root; polyphony:impl-merged-in-mg=pg-1; PG-1");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.ClearImplMerged(workItem: 100, mgPath: "pg-1"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "branch_clear_impl_merged" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("workitem:100");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.BranchClearImplMergedPayload);
        payload.ShouldNotBeNull();
        payload.WasMutated.ShouldBeTrue();
        payload.AlreadyInDesiredState.ShouldBeFalse();
        entry.Effects.Count.ShouldBe(1);
        AssertEffect(entry.Effects[0], ResourceKind.AdoWorkItemTag, "100:polyphony:impl-merged-in-mg=pg-1", ResourceIntent.EnsureAbsent, ResourceMutation.DeletedNow, polyphonyOwned: true);
    }

    [Fact]
    public async Task NextImpl_TransitionsTask_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubBranch(runner, "");
        ExpectStateTransition(runner, 300, "Doing");

        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("My Epic").WithState("Doing");
        var issue = new WorkItemBuilder().WithId(200).WithType("Issue").WithTitle("Issue 1")
            .WithState("Doing").WithParentId(100);
        var task = new WorkItemBuilder().WithId(300).WithType("Task").WithTitle("First Task")
            .WithState("To Do").WithTags("PG-1").WithParentId(200);
        await SeedAsync(epic.Build(), issue.Build(), task.Build());

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.NextImpl(workItem: 100, pgName: "PG-1"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "branch_next_impl" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("workitem:100");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.BranchNextImplPayload);
        payload.ShouldNotBeNull();
        payload.SelectedWorkItemId.ShouldBe(300);
        payload.TargetState.ShouldBe("Doing");
        payload.WasMutated.ShouldBeTrue();
        entry.Effects.Count.ShouldBe(2);
        AssertEffect(entry.Effects[0], ResourceKind.AdoWorkItemState, "workitem:300", ResourceIntent.SetState, ResourceMutation.Changed, polyphonyOwned: true);
        AssertEffect(entry.Effects[1], ResourceKind.AdoWorkItem, "workitem:300", ResourceIntent.Observe, ResourceMutation.NoChangedAlreadySatisfied, polyphonyOwned: false);
    }

    private static void AssertEffect(
        JournalResourceEffect effect,
        string kind,
        string id,
        ResourceIntent intent,
        ResourceMutation mutation,
        bool polyphonyOwned)
    {
        effect.Kind.ShouldBe(kind);
        effect.Id.ShouldBe(id);
        effect.Intent.ShouldBe(intent);
        effect.Mutation.ShouldBe(mutation);
        effect.PolyphonyOwned.ShouldBe(polyphonyOwned);
    }

    private (BranchCommands Command, FakeProcessRunner Runner, JournalStore Store) CreateCommand(ProcessConfig? cfg = null, string runId = "run-journal")
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var config = cfg ?? Config;
        var walker = new HierarchyWalker(config, Repository);
        var validator = new TransitionValidator(config);
        var store = CreateJournalStore();
        var command = new BranchCommands(
            twig,
            walker,
            Repository,
            validator,
            git,
            config,
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            new Polyphony.Sdlc.Observers.PullRequestReader(gh, null),
            new RunContext(runId),
            new JournaledActionDecorator(store));
        return (command, runner, store);
    }

    private JournalStore CreateJournalStore()
    {
        var dir = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _createdDirs.Add(dir);
        return new JournalStore(Path.Combine(dir, ".polyphony-state", "journal.db"));
    }

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", branch],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubLocalBranchExists(FakeProcessRunner runner, string branch, bool exists, string sha)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(exists ? 0 : 1, exists ? sha + "\n" : "", exists ? "" : "fatal: needed a single revision"));

    private static void StubLocalBranchExistsSequence(FakeProcessRunner runner, string branch, bool existsBefore, string finalSha)
        => runner.WhenStartsWithSequence(
            "git",
            ["rev-parse", "--verify", $"refs/heads/{branch}"],
            existsBefore
                ? new ProcessResult(0, finalSha + "\n", "")
                : new ProcessResult(1, "", "fatal: needed a single revision"),
            new ProcessResult(0, finalSha + "\n", ""));

    private static void StubCheckout(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));

    private static void StubCheckoutTracking(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", "--track", $"origin/{branch}"], new ProcessResult(0, "", ""));

    private static void StubCreateBranch(FakeProcessRunner runner, string branch, string startPoint)
        => runner.WhenExact("git", ["checkout", "-b", branch, startPoint], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubFetch(FakeProcessRunner runner, string refspec)
        => runner.WhenExact("git", ["fetch", "origin", refspec], new ProcessResult(0, "", ""));

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

    private static void StubTagsRoundTrip(FakeProcessRunner runner, int workItemId, string initialTags)
    {
        var state = new[] { initialTags };

        runner.WhenAsync(
            (e, a) => e == "twig"
                && a.Count >= 4
                && a[0] == "show"
                && a[1] == workItemId.ToString()
                && a[^1] == "json",
            (_, _) =>
            {
                var encoded = JsonEncodedText.Encode(state[0]).Value;
                var json = $$"""{"id":{{workItemId}},"tags":"{{encoded}}"}""";
                return Task.FromResult(new ProcessResult(0, json, ""));
            });

        runner.WhenAsync(
            (e, a) => e == "twig"
                && a.Count >= 5
                && a[0] == "patch"
                && a[1] == "--id"
                && a[2] == workItemId.ToString()
                && a[3] == "--json",
            (args, _) =>
            {
                using var doc = JsonDocument.Parse(args[4]);
                if (doc.RootElement.TryGetProperty("System.Tags", out var tagsEl))
                {
                    state[0] = tagsEl.GetString() ?? state[0];
                }
                return Task.FromResult(new ProcessResult(0, "{}", ""));
            });
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var dir in _createdDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
