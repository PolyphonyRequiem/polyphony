using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Routing;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Services;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class Phase3CommandsJournalTests : CommandTestBase
{
    private readonly string _scratchRoot = Path.Combine(AppContext.BaseDirectory, "phase3-journal-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _createdDirs = [];

    [Fact]
    public async Task SeedChildren_CreatesChild_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreatePlanCommand();
        StubShowTreeNoChildren(runner, 100);
        StubCreateChild(runner, 555);
        StubShowParent(runner, 100, "");
        StubPatchOk(runner);

        var (exitCode, _) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(100, "[{\"child_id\":\"task-1\",\"title\":\"Do thing\",\"type\":\"Task\",\"description\":\"Body.\"}]"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "plan_seed_children" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("workitem:100");
        entry.Outcome.ShouldBe(JournalOutcome.Success);

        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PlanSeedChildrenPayload);
        payload.ShouldNotBeNull();
        payload.WorkItemId.ShouldBe(100);
        payload.SeededItems.Count.ShouldBe(1);
        payload.SeededItems[0].WorkItemId.ShouldBe(555);
        payload.WasMutated.ShouldBeTrue();

        entry.Effects.ShouldContain(effect => effect.Kind == ResourceKind.AdoWorkItem
            && effect.Id == "workitem:555"
            && effect.Mutation == ResourceMutation.CreatedNow
            && effect.PolyphonyOwned);
        entry.Effects.ShouldContain(effect => effect.Kind == ResourceKind.AdoWorkItemTag
            && effect.Id == "100:polyphony:planned"
            && effect.Mutation == ResourceMutation.CreatedNow);
    }

    [Fact]
    public async Task WorktreeAdd_WithRef_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateWorktreeCommand();
        runner.WhenExact("git", ["worktree", "add", "-b", "feature/x", "C:/wt/x", "origin/main"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Add(branch: "feature/x", path: "C:/wt/x", gitRef: "origin/main"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "worktree_add" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("C:/wt/x");
        entry.Outcome.ShouldBe(JournalOutcome.Success);

        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.WorktreeAddPayload);
        payload.ShouldNotBeNull();
        payload.Branch.ShouldBe("feature/x");
        payload.Path.ShouldBe("C:/wt/x");
        payload.GitRef.ShouldBe("origin/main");

        entry.Effects.Count.ShouldBe(1);
        var effect = entry.Effects[0];
        effect.Kind.ShouldBe(ResourceKind.GitWorktree);
        effect.Id.ShouldBe("C:/wt/x");
        effect.Intent.ShouldBe(ResourceIntent.EnsurePresent);
        effect.Mutation.ShouldBe(ResourceMutation.CreatedNow);
        effect.PolyphonyOwned.ShouldBeTrue();
    }

    [Fact]
    public async Task ResetState_Execute_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateResetCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root; polyphony:run-started-at=2024-01-01T00:00:00.000Z");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.ResetState(root: 100, execute: true));
        var entries = await store.QueryAsync(new JournalQuery { Action = "reset_state" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("workitem:100");
        entry.Outcome.ShouldBe(JournalOutcome.Success);

        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.ResetStatePayload);
        payload.ShouldNotBeNull();
        payload.Root.ShouldBe(100);
        payload.DryRun.ShouldBeFalse();
        payload.NewWatermark.ShouldNotBeNullOrWhiteSpace();

        entry.Effects.Count.ShouldBe(1);
        var effect = entry.Effects[0];
        effect.Kind.ShouldBe(ResourceKind.AdoWorkItemTag);
        effect.Id.ShouldStartWith("100:polyphony:run-started-at=");
        effect.Intent.ShouldBe(ResourceIntent.EnsurePresent);
        effect.Mutation.ShouldBe(ResourceMutation.Changed);
        effect.PolyphonyOwned.ShouldBeTrue();
    }

    private (PlanCommands Command, FakeProcessRunner Runner, JournalStore Store) CreatePlanCommand(string runId = "run-journal")
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var store = CreateJournalStore();
        var walker = new HierarchyWalker(Config, Repository);
        var command = new PlanCommands(
            walker,
            Repository,
            Config,
            twig,
            git,
            gh,
            new ThrowingAdoClient(),
            new FakePostconditionVerifier(),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new RepoIdentityResolver(git),
            new PullRequestReader(gh, null),
            new RunContext(runId),
            new JournaledActionDecorator(store));
        return (command, runner, store);
    }

    private (WorktreeCommands Command, FakeProcessRunner Runner, JournalStore Store) CreateWorktreeCommand(string runId = "run-journal")
    {
        var runner = new FakeProcessRunner();
        var git = new GitClient(runner);
        var store = CreateJournalStore();
        var command = new WorktreeCommands(git, new RunContext(runId), new JournaledActionDecorator(store));
        return (command, runner, store);
    }

    private (ResetCommands Command, FakeProcessRunner Runner, JournalStore Store) CreateResetCommand(string runId = "run-journal")
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var store = CreateJournalStore();
        var pullRequestReader = new PullRequestReader(gh, null);
        var resolver = new RepoIdentityResolver(git);
        var planObserver = new PlanObserver(git, gh, new ThrowingAdoClient(), twig, resolver);
        var walker = new HierarchyWalker(Config, Repository);
        var command = new ResetCommands(twig, git, pullRequestReader, planObserver, walker, new RunContext(runId), new JournaledActionDecorator(store));
        return (command, runner, store);
    }

    private JournalStore CreateJournalStore()
    {
        var dir = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _createdDirs.Add(dir);
        return new JournalStore(Path.Combine(dir, ".polyphony-state", "journal.db"));
    }

    private static void StubShowTreeNoChildren(FakeProcessRunner runner, int parentId)
        => runner.WhenExact("twig", ["show", parentId.ToString(), "--tree", "--output", "json"],
            new ProcessResult(0, $$"""{"id":{{parentId}},"title":"Parent","children":[]}""", ""));

    private static void StubShowParent(FakeProcessRunner runner, int parentId, string? tagsField)
    {
        var tags = tagsField is null ? "null" : $"\"{tagsField}\"";
        runner.WhenExact("twig", ["show", parentId.ToString(), "--output", "json"],
            new ProcessResult(0, $$"""{"id":{{parentId}},"title":"Parent","tags":{{tags}}}""", ""));
    }

    private static void StubPatchOk(FakeProcessRunner runner)
    {
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));
        StubSync(runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    private static void StubCreateChild(FakeProcessRunner runner, int newId)
        => runner.WhenStartsWith("twig", ["new"],
            new ProcessResult(0, $$"""{"id":{{newId}},"title":"Created"}""", ""));

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
