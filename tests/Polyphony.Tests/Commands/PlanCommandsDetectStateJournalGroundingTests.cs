using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W3 (AB#3277): <c>plan detect-state</c> must return <c>not_started</c>
/// with <c>lineage_anchor = "journal"</c> whenever the current run's
/// journal carries zero plan-action rows for the item — regardless of
/// whatever PR/branch/tag artifacts a prior run left behind. This is the
/// MVP cut: just the short-circuit + lineage_anchor field; W4 will extend
/// journal-grounding to the full state machine.
/// </summary>
public sealed class PlanCommandsDetectStateJournalGroundingTests : CommandTestBase, IDisposable
{
    private const int RootId = 1000;
    private const int ChildId = 2000;
    private const string ChildPlanBranch = "plan/1000-2000";

    private readonly string tempCommonDir;

    public PlanCommandsDetectStateJournalGroundingTests()
    {
        this.tempCommonDir = Path.Combine(Path.GetTempPath(), $"polytest-j-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempCommonDir);
        Directory.CreateDirectory(Path.Combine(this.tempCommonDir, "polyphony", RootId.ToString()));
    }

    public override void Dispose()
    {
        try { if (Directory.Exists(this.tempCommonDir)) Directory.Delete(this.tempCommonDir, recursive: true); } catch { }
        base.Dispose();
    }

    private sealed class StubJournalStore : IJournalStore
    {
        private readonly IReadOnlyList<JournalEntry> _entries;
        public StubJournalStore(IReadOnlyList<JournalEntry> entries) { _entries = entries; }

        public string DatabasePath => "stub.db";
        public Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct) => Task.FromResult(0L);
        public Task RecordEndAsync(long actionId, JournalOutcome outcome, string? errorCode, string? errorMessage, string? payloadJson, IReadOnlyList<JournalResourceEffect>? effects, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct)
        {
            // Match the verb's filters so the stub behaves like a real
            // journal under the same query.
            var matched = _entries
                .Where(e => query.RunId is null || e.RunId == query.RunId)
                .Where(e => !query.RootId.HasValue || e.RootId == query.RootId)
                .Where(e => !query.WorkItemId.HasValue || e.WorkItemId == query.WorkItemId)
                .ToList();
            return Task.FromResult<IReadOnlyList<JournalEntry>>(matched);
        }
        public Task ExportAsync(string destinationPath, CancellationToken ct) => Task.CompletedTask;
    }

    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand(
        IJournalStore journalStore,
        RunContext runContext)
    {
        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, this.tempCommonDir + "\n", ""));
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PlanCommands(
            walker, Repository, Config, twig, git, gh, new ThrowingAdoClient(),
            new FakePostconditionVerifier(),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            new Polyphony.Sdlc.Observers.PullRequestReader(gh, null),
            runContext: runContext,
            journalDecorator: null,
            journalStore: journalStore),
            runner);
    }

    private static PlanDetectStateResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDetectStateResult)!;

    private static RunContext LauncherRunContext(string runId)
    {
        // The internal RunContext(string) ctor stamps Source=Explicit, which
        // is what a launcher-supplied run-id looks like to RunContext.HasManualLineage
        // (it returns false for non-manual-prefixed ids).
        var ctorInfo = typeof(RunContext).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null, [typeof(string)], modifiers: null)!;
        return (RunContext)ctorInfo.Invoke([runId]);
    }

    [Fact]
    public async Task EmptyCurrentLineageJournal_ReturnsNotStarted_WithLineageAnchorJournal()
    {
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([]),
            LauncherRunContext("01JZ7P0X9KZBKQR4N3FVMT8YWA"));

        // Critical: do NOT stub any PR / branch / tag calls. If the short-
        // circuit doesn't fire, the verb would try them and fail.
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("journal");
        result.PlanBranch.ShouldBe(ChildPlanBranch);
        result.BranchExistsOnOrigin.ShouldBeFalse();
    }

    [Fact]
    public async Task ManualLineage_SkipsJournalShortCircuit_FallsThroughToArchaeology()
    {
        // A manual_<guid> fallback id means the launcher did NOT stamp; we
        // cannot trust an empty journal under that lineage, so the verb
        // must fall through to PR/branch observation (and the archaeology
        // path now annotates lineage_anchor=archaeology).
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([]),
            new RunContext(_ => null));   // unstamped → manual_<guid> fallback

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: false);
        StubPrListEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("archaeology");
    }

    [Fact]
    public async Task PriorRunMergedPr_IgnoredWhenCurrentLineageJournalEmpty()
    {
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([]),
            LauncherRunContext("01JZ7P0X9KZBKQR4N3FVMT8YWA"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("journal");
        runner.Invocations.ShouldNotContain(i => i.Executable == "gh" && i.Arguments.Contains("list"));
    }

    [Fact]
    public async Task CurrentLineageHasJournalRows_FallsThroughToArchaeology()
    {
        var runId = "01JZ7P0X9KZBKQR4N3FVMT8YWA";
        var entry = new JournalEntry
        {
            Id = 1,
            RunId = runId,
            RootId = RootId,
            WorkItemId = ChildId,
            Action = "plan write-plan",
            Target = $"workitem:{ChildId}",
            StartedAt = 1_700_000_000,
            FinishedAt = 1_700_000_001,
            Outcome = JournalOutcome.Success,
        };
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([entry]),
            LauncherRunContext(runId));

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: false);
        StubPrListEmpty(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("archaeology");
    }

    [Fact]
    public async Task PriorLineageJournalRows_DoNotCount()
    {
        var priorEntry = new JournalEntry
        {
            Id = 1,
            RunId = "01JOLDRUNXXXXXXXXXXXXXXXXX1",
            RootId = RootId,
            WorkItemId = ChildId,
            Action = "plan write-plan",
            Target = $"workitem:{ChildId}",
            StartedAt = 1_600_000_000,
            FinishedAt = 1_600_000_001,
            Outcome = JournalOutcome.Success,
        };
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([priorEntry]),
            LauncherRunContext("01JZ7P0X9KZBKQR4N3FVMT8YWA"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("journal");
    }

    // ─── Stubs (mirror PlanCommandsDetectStateTests for the archaeology cases) ─

    private static void StubRemoteUrl(FakeProcessRunner runner, string url = "https://github.com/acme/repo.git")
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
}
