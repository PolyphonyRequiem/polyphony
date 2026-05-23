using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W3+W4 (AB#3277/AB#3278): journal-grounded <c>plan detect-state</c>.
/// Covers all eight B1 walk-through cases. The journal anchors the answer
/// to "did THIS lineage open/merge/finish-planning for this item?"; ADO/git
/// supply live status only for journal-grounded objects. Tests pin one
/// case each.
/// </summary>
public sealed class PlanCommandsDetectStateJournalGroundingTests : CommandTestBase, IDisposable
{
    private const int RootId = 1000;
    private const int ChildId = 2000;
    private const string ChildPlanBranch = "plan/1000-2000";
    private const int JournalPrNumber = 555;
    private const string CurrentRunId = "01JZ7P0X9KZBKQR4N3FVMT8YWA";
    private const string PriorRunId = "01JOLDRUNXXXXXXXXXXXXXXXX1";

    private readonly string tempCommonDir;
    private readonly string localManifestPath;

    public PlanCommandsDetectStateJournalGroundingTests()
    {
        this.tempCommonDir = Path.Combine(Path.GetTempPath(), $"polytest-j-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempCommonDir);
        var manifestDir = Path.Combine(this.tempCommonDir, "polyphony", RootId.ToString());
        Directory.CreateDirectory(manifestDir);
        this.localManifestPath = Path.Combine(manifestDir, "run.yaml");
    }

    public override void Dispose()
    {
        try { if (Directory.Exists(this.tempCommonDir)) Directory.Delete(this.tempCommonDir, recursive: true); } catch { }
        base.Dispose();
    }

    // ─── Test infrastructure ──────────────────────────────────────────────

    private sealed class StubJournalStore : IJournalStore
    {
        private readonly IReadOnlyList<JournalEntry> _entries;
        private readonly Exception? _throwOnQuery;

        public StubJournalStore(IReadOnlyList<JournalEntry> entries, Exception? throwOnQuery = null)
        {
            _entries = entries;
            _throwOnQuery = throwOnQuery;
        }

        public string DatabasePath => "stub.db";
        public Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct) => Task.FromResult(0L);
        public Task RecordEndAsync(long actionId, JournalOutcome outcome, string? errorCode, string? errorMessage, string? payloadJson, IReadOnlyList<JournalResourceEffect>? effects, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct)
        {
            if (_throwOnQuery is not null) throw _throwOnQuery;
            var matched = _entries
                .Where(e => query.RunId is null || e.RunId == query.RunId)
                .Where(e => !query.RootId.HasValue || e.RootId == query.RootId)
                .Where(e => !query.WorkItemId.HasValue || e.WorkItemId == query.WorkItemId)
                .ToList();
            return Task.FromResult<IReadOnlyList<JournalEntry>>(matched);
        }
        public Task ExportAsync(string destinationPath, CancellationToken ct) => Task.CompletedTask;
        public Task RecordLineageAsync(string runId, int rootId, string? host, string? user, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<JournalLineage>> GetLineagesAsync(int rootId, CancellationToken ct) => Task.FromResult<IReadOnlyList<JournalLineage>>([]);
        public Task<bool> RetireLineageAsync(string runId, int rootId, string? reason, CancellationToken ct) => Task.FromResult(false);
    }

    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand(
        IJournalStore journalStore, RunContext runContext)
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
        // is what a launcher-supplied run-id looks like (HasManualLineage=false).
        var ctorInfo = typeof(RunContext).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null, [typeof(string)], modifiers: null)!;
        return (RunContext)ctorInfo.Invoke([runId]);
    }

    // ─── Journal-entry factories ──────────────────────────────────────────

    private static JournalEntry Entry(long id, string runId, string action, string? payloadJson = null,
        JournalOutcome outcome = JournalOutcome.Success) =>
        new()
        {
            Id = id,
            RunId = runId,
            RootId = RootId,
            WorkItemId = ChildId,
            Action = action,
            Target = $"workitem:{ChildId}",
            StartedAt = 1_700_000_000 + id,
            FinishedAt = 1_700_000_000 + id + 1,
            Outcome = outcome,
            PayloadJson = payloadJson,
        };

    private static string OpenPlanPrPayload(int prNumber) =>
        JsonSerializer.Serialize(new PrOpenPlanPrPayload
        {
            RootId = RootId,
            ItemId = ChildId,
            ParentItemId = RootId,
            ItemKey = $"{ChildId}",
            IsRootPlan = false,
            HeadBranch = ChildPlanBranch,
            BaseBranch = "feature/1000",
            PrNumber = prNumber,
            PrUrl = $"https://github.com/acme/repo/pull/{prNumber}",
            ResultAction = "opened",
            Succeeded = true,
            WasMutated = true,
            Stale = false,
        }, PolyphonyJsonContext.Default.PrOpenPlanPrPayload);

    private static string MergePlanPrPayload(int prNumber) =>
        JsonSerializer.Serialize(new PrMergePlanPrPayload
        {
            RootId = RootId,
            ItemId = ChildId,
            ParentItemId = RootId,
            PrNumber = prNumber,
            ItemKey = $"{ChildId}",
            IsRootPlan = false,
            HeadBranch = ChildPlanBranch,
            BaseBranch = "feature/1000",
            ManifestBranch = "feature/1000",
            PrUrl = $"https://github.com/acme/repo/pull/{prNumber}",
            ResultAction = "merged",
            Succeeded = true,
            WasMutated = true,
            AlreadyMerged = false,
        }, PolyphonyJsonContext.Default.PrMergePlanPrPayload);

    private static string SeedChildrenPayload(bool planningCompleted) =>
        JsonSerializer.Serialize(new PlanSeedChildrenPayload
        {
            WorkItemId = ChildId,
            ChildCount = 1,
            SeededItems = [],
            ReusedItems = [],
            Errors = [],
            Warnings = [],
            PlannedTagMutated = planningCompleted,
            PlannedTagAlreadyPresent = false,
            RootFacets = [],
            FacetsTagMutated = false,
            Succeeded = true,
            WasMutated = true,
            PlanningCompleted = planningCompleted,
        }, PolyphonyJsonContext.Default.PlanSeedChildrenPayload);

    // ─── Process stubs (used by W4 paths that DO poll a journal-anchored PR) ─

    private static void StubRemoteUrl(FakeProcessRunner runner, string url = "https://github.com/acme/repo.git")
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrPoll(FakeProcessRunner runner, int prNumber, string state, string body = "")
    {
        var bodyJson = JsonEncodedText.Encode(body).Value;
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "REVIEW_REQUIRED",
              "mergeable": "MERGEABLE",
              "headRefName": "{{ChildPlanBranch}}",
              "headRefOid": "abc123",
              "baseRefName": "feature/1000",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "{{bodyJson}}",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private static void StubPrPollNotFound(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(1, "", "no pull request found"));

    private void WriteManifest(IDictionary<string, int> planGenerations)
    {
        var manifest = new Polyphony.Manifest.RunManifest
        {
            Schema = 1,
            RootId = RootId,
            PlatformProject = "github.com/acme/repo",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = new Dictionary<string, int>(planGenerations, StringComparer.Ordinal),
        };
        Polyphony.Manifest.RunManifestStore.Save(this.localManifestPath, manifest);
    }

    // ═════════════════════════════════════════════════════════════════════
    // B1 case 1 / 7: no journal rows → not_started (THE bug fix).
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoJournalRows_ReturnsNotStarted_WithLineageAnchorJournal()
    {
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([]),
            LauncherRunContext(CurrentRunId));

        // Critical: do NOT stub any PR / branch / tag calls. If the short-
        // circuit doesn't fire, the verb would try them and fail.
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("journal");
        result.BranchExistsOnOrigin.ShouldBeFalse();
    }

    [Fact]
    public async Task PriorLineageJournalRows_DoNotCount_ReturnsNotStarted()
    {
        // Rows exist but under a DIFFERENT run id → equivalent to "no rows
        // for current lineage". This is the residue-from-prior-run case.
        var (cmd, _) = CreateCommand(
            new StubJournalStore([Entry(1, PriorRunId, "plan_write_plan")]),
            LauncherRunContext(CurrentRunId));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("journal");
    }

    // ═════════════════════════════════════════════════════════════════════
    // B1 case 2 / 8: journal opened PR; ADO confirms OPEN → awaiting_review.
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task JournalOpenedPr_AdoOpen_ReturnsAwaitingReview_PollsJournalPrNumber()
    {
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([Entry(1, CurrentRunId, "pr_open_plan_pr", OpenPlanPrPayload(JournalPrNumber))]),
            LauncherRunContext(CurrentRunId));

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrPoll(runner, JournalPrNumber, "OPEN");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("awaiting_review");
        result.PrNumber.ShouldBe(JournalPrNumber);
        result.LineageAnchor.ShouldBe("journal");

        // The verb must consult ONLY the journal-anchored PR number — no
        // archaeological `gh pr list` should have happened.
        runner.Invocations.ShouldNotContain(i => i.Executable == "gh" && i.Arguments.Contains("list"));
    }

    // ═════════════════════════════════════════════════════════════════════
    // B1 case 2 variant: journal opened PR; stale generation flag.
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task JournalOpenedPr_AdoOpen_StaleGeneration_ReturnsStaleGeneration()
    {
        // Manifest plan-generations: root has advanced past the snapshot.
        WriteManifest(new Dictionary<string, int> { ["root"] = 2 });

        // PR body carries front-matter that points at snapshot gen 1.
        var prBody = "---\nancestor_plan_generations:\n  root: 1\n---\nbody";

        var (cmd, runner) = CreateCommand(
            new StubJournalStore([Entry(1, CurrentRunId, "pr_open_plan_pr", OpenPlanPrPayload(JournalPrNumber))]),
            LauncherRunContext(CurrentRunId));

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrPoll(runner, JournalPrNumber, "OPEN", body: prBody);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("stale_generation");
        result.LineageAnchor.ShouldBe("journal");
        result.StaleAncestors.ShouldContain("root: snapshot=1, manifest=2");
    }

    // ═════════════════════════════════════════════════════════════════════
    // B1 case 3 / 4: journal says merged but NOT planning-completed.
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task JournalMerged_NoPlanningCompleted_ReturnsMergedUnseeded()
    {
        var (cmd, _) = CreateCommand(
            new StubJournalStore([
                Entry(1, CurrentRunId, "pr_open_plan_pr", OpenPlanPrPayload(JournalPrNumber)),
                Entry(2, CurrentRunId, "pr_merge_plan_pr", MergePlanPrPayload(JournalPrNumber)),
            ]),
            LauncherRunContext(CurrentRunId));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("merged_unseeded");
        result.PrNumber.ShouldBe(JournalPrNumber);
        result.PrState.ShouldBe("MERGED");
        result.LineageAnchor.ShouldBe("journal");
    }

    // ═════════════════════════════════════════════════════════════════════
    // B1 case 5: journal says planning completed → complete (no
    // parent-change overlay because no children).
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task JournalPlanningCompleted_ReturnsComplete()
    {
        WriteManifest(new Dictionary<string, int> { [ChildId.ToString()] = 1 });

        var (cmd, runner) = CreateCommand(
            new StubJournalStore([
                Entry(1, CurrentRunId, "pr_open_plan_pr", OpenPlanPrPayload(JournalPrNumber)),
                Entry(2, CurrentRunId, "pr_merge_plan_pr", MergePlanPrPayload(JournalPrNumber)),
                Entry(3, CurrentRunId, "plan_seed_children", SeedChildrenPayload(planningCompleted: true)),
            ]),
            LauncherRunContext(CurrentRunId));

        // Child-PR overlay needs origin + twig (no children → empty list).
        StubRemoteUrl(runner);
        runner.WhenExact("twig", ["show", ChildId.ToString(), "--tree", "--output", "json"],
            new ProcessResult(0, $$"""{"id":{{ChildId}},"title":"Item","children":[]}""", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("complete");
        result.LineageAnchor.ShouldBe("journal");
    }

    // ═════════════════════════════════════════════════════════════════════
    // B1 case 6: journal opened PR; ADO ABANDONED/CLOSED.
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task JournalOpenedPr_AdoClosed_BranchExists_ReturnsClosedUnmerged()
    {
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([Entry(1, CurrentRunId, "pr_open_plan_pr", OpenPlanPrPayload(JournalPrNumber))]),
            LauncherRunContext(CurrentRunId));

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrPoll(runner, JournalPrNumber, "CLOSED");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("closed_unmerged");
        result.PrState.ShouldBe("CLOSED");
        result.LineageAnchor.ShouldBe("journal");
    }

    [Fact]
    public async Task JournalOpenedPr_AdoClosed_BranchGone_ReturnsNotStarted()
    {
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([Entry(1, CurrentRunId, "pr_open_plan_pr", OpenPlanPrPayload(JournalPrNumber))]),
            LauncherRunContext(CurrentRunId));

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: false);
        StubPrPoll(runner, JournalPrNumber, "CLOSED");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("journal");
    }

    // ═════════════════════════════════════════════════════════════════════
    // B1 case 4 variant: journal opened PR; ADO MERGED (crash recovery — no
    // pr_merge_plan_* row was written).
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task JournalOpenedPr_AdoMerged_NoMergeRow_ReturnsMergedUnseeded()
    {
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([Entry(1, CurrentRunId, "pr_open_plan_pr", OpenPlanPrPayload(JournalPrNumber))]),
            LauncherRunContext(CurrentRunId));

        StubRemoteUrl(runner);
        StubLsRemote(runner, ChildPlanBranch, exists: true);
        StubPrPoll(runner, JournalPrNumber, "MERGED");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("merged_unseeded");
        result.LineageAnchor.ShouldBe("journal");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Journal rows exist but no PR-open row yet (e.g. only plan_write_plan).
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task JournalRowsButNoPrOpen_ReturnsNotStarted()
    {
        var (cmd, _) = CreateCommand(
            new StubJournalStore([Entry(1, CurrentRunId, "plan_write_plan")]),
            LauncherRunContext(CurrentRunId));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.DetectState(rootId: RootId, itemId: ChildId));

        exit.ShouldBe(0);
        var result = Parse(output);
        result.State.ShouldBe("not_started");
        result.LineageAnchor.ShouldBe("journal");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Fall-back posture: manual lineage and journal-throw both fall through
    // to archaeology so the legacy verb still works.
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ManualLineage_SkipsJournalGrounding_FallsThroughToArchaeology()
    {
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
    public async Task JournalThrows_FailsOpenToArchaeology()
    {
        var (cmd, runner) = CreateCommand(
            new StubJournalStore([], throwOnQuery: new InvalidOperationException("simulated journal failure")),
            LauncherRunContext(CurrentRunId));

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
}
