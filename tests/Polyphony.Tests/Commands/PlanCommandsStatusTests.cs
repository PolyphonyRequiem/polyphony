using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for the redesigned tree-walking <c>polyphony plan
/// status</c> verb (Phase 3 P10).
///
/// <para>The verb walks the in-scope subtree from <c>--root</c>, derives
/// each item's plannable facet from the loaded
/// <see cref="ProcessConfig"/>, and queries <c>gh</c> per plan branch.
/// Tests stub all shell-outs via <see cref="FakeProcessRunner"/> so the
/// suite stays hermetic and deterministic.</para>
///
/// <para>Three categories of coverage:
/// <list type="number">
///   <item>Routing-style envelope guarantees — every error path
///         exits 0 with a populated <c>error_code</c>.</item>
///   <item>Tree-walk correctness — n/a vs needed vs open vs merged
///         vs abandoned, and the four corresponding summary counters
///         plus pending_revisions for OPEN + CHANGES_REQUESTED.</item>
///   <item>UX flags — <c>--include-na</c> filtering, <c>--json</c>
///         vs human output, <c>--repo</c> override.</item>
/// </list></para>
/// </summary>
public sealed class PlanCommandsStatusTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly string _manifestPath;

    public PlanCommandsStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyphony-plan-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "run.yaml");
    }

    void IDisposable.Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        base.Dispose();
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private (PlanCommands Cmd, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner), new ThrowingAdoClient(), new FakePostconditionVerifier(), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(new GitClient(runner)), new Polyphony.Sdlc.Observers.RepoIdentityResolver(new GitClient(runner)), new Polyphony.Sdlc.Observers.PullRequestReader(new GhClient(runner), null)),
                runner);
    }

    private void SaveManifest(int rootId, Dictionary<string, int>? planGenerations = null)
    {
        var manifest = new RunManifest
        {
            Schema = 1,
            RootId = rootId,
            PlatformProject = "github.com/owner/repo",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = planGenerations ?? new Dictionary<string, int>(StringComparer.Ordinal),
            MergedPlanPrs = new List<MergedPlanPrEntry>(),
        };
        RunManifestStore.Save(_manifestPath, manifest);
    }

    private static PlanStatusResult ParseJson(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanStatusResult)!;

    /// <summary>Stubs git origin to a github.com remote so slug resolution succeeds.</summary>
    private static void StubOrigin(FakeProcessRunner runner, string slug = "owner/repo")
        => runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, $"https://github.com/{slug}.git\n", ""));

    /// <summary>
    /// Stubs <c>gh pr list ... --state STATE</c>. Matches by checking that the
    /// invocation contains both <c>--state</c> and the requested state value
    /// (so per-state stubs can coexist on a single runner).
    /// </summary>
    private static void StubPrList(FakeProcessRunner runner, string state, string responseJson)
        => runner.When(
            (exe, args) =>
                exe == "gh"
                && args.Count >= 2 && args[0] == "pr" && args[1] == "list"
                && ContainsArg(args, "--state", state),
            new ProcessResult(0, responseJson, ""));

    /// <summary>Stubs gh pr view (poll-data) for a specific PR number with the given review decision.</summary>
    private static void StubPrPollData(FakeProcessRunner runner, int prNumber, string reviewDecision)
    {
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "OPEN",
              "reviewDecision": "{{reviewDecision}}",
              "mergeable": "MERGEABLE",
              "headRefName": "plan/test",
              "headRefOid": "abc123",
              "baseRefName": "main",
              "mergeCommit": null,
              "mergedAt": null,
              "body": "",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(0, json, ""));
    }

    private static bool ContainsArg(IReadOnlyList<string> args, string flag, string value)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == flag && args[i + 1] == value) return true;
        }
        return false;
    }

    private static string PrJson(int number, string headBranch, int? mergedAtYear = null)
    {
        var merged = mergedAtYear is int y
            ? $"\"{y:0000}-01-01T00:00:00Z\""
            : "null";
        return $$"""
            [{
              "number": {{number}},
              "headRefName": "{{headBranch}}",
              "url": "https://github.com/owner/repo/pull/{{number}}",
              "mergedAt": {{merged}}
            }]
            """;
    }

    // ─── Input validation ──────────────────────────────────────────────

    [Fact]
    public async Task RootMustBePositive()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 0, manifest: _manifestPath, json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task RootNotFound_InTwigCache()
    {
        // No items seeded — root walk fails immediately.
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 999, manifest: "", json: true));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("root_not_found");
        result.RootId.ShouldBe(999);
    }

    // ─── Manifest interaction ──────────────────────────────────────────

    [Fact]
    public async Task ExplicitManifestPath_NotFound_EmitsManifestNotFound()
    {
        // Seed a root so the BFS walk succeeds and the verb actually
        // reaches the manifest-load step that this test cares about.
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var (cmd, _) = CreateCommand();
        var missing = Path.Combine(_tempDir, "no-such.yaml");
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: missing, json: true));
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("manifest_not_found");
    }

    [Fact]
    public async Task Manifest_RootIdMismatch_EmitsError()
    {
        // Seed one item so the BFS walk succeeds before manifest validation.
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        SaveManifest(rootId: 555);
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: _manifestPath, json: true));
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("root_id_mismatch");
        result.ErrorMessage!.ShouldContain("555");
        result.ErrorMessage!.ShouldContain("100");
    }

    [Fact]
    public async Task ManifestProvidesGeneration_ForOpenPr()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Apex").Build());
        SaveManifest(rootId: 100, planGenerations: new() { ["root"] = 7 });
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", PrJson(42, "plan/100"));
        StubPrPollData(runner, 42, "APPROVED");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: _manifestPath, json: true));
        var result = ParseJson(output);
        result.Success.ShouldBeTrue();
        result.Items.Single().PlanGeneration.ShouldBe(7);
    }

    // ─── Plannable facet classification ───────────────────────────────

    [Fact]
    public async Task NonPlannableRoot_ReportsNa_HiddenByDefault()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Task").WithTitle("Just a task").Build());
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        result.Success.ShouldBeTrue();
        result.Items.ShouldBeEmpty();
        result.Summary.TotalItems.ShouldBe(1);
        result.Summary.PlanNa.ShouldBe(1);
    }

    [Fact]
    public async Task NonPlannableRoot_IncludeNa_ShowsRow()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Task").WithTitle("Just a task").Build());
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", includeNa: true, json: true));
        var result = ParseJson(output);
        result.Items.Count.ShouldBe(1);
        result.Items[0].PlanStatus.ShouldBe("n/a");
        result.Items[0].PlanPrNumber.ShouldBeNull();
    }

    // ─── PR-state classification ──────────────────────────────────────

    [Fact]
    public async Task PlannableRoot_NoPr_ReportsNeeded()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Apex").Build());
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", "[]");
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "closed", "[]");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        result.Items.Single().PlanStatus.ShouldBe("needed");
        result.Items[0].PlanGeneration.ShouldBeNull();
        result.Summary.PlanNeeded.ShouldBe(1);
    }

    [Fact]
    public async Task PlannableRoot_OpenPr_NoChangesRequested_Pending_False()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", PrJson(42, "plan/100"));
        StubPrPollData(runner, 42, "APPROVED");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        var row = result.Items.Single();
        row.PlanStatus.ShouldBe("open");
        row.PlanPrNumber.ShouldBe(42);
        row.PendingRevisions.ShouldBe(false);
        result.Summary.PendingRevisions.ShouldBe(0);
    }

    [Fact]
    public async Task PlannableRoot_OpenPr_ChangesRequested_Pending_True()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", PrJson(42, "plan/100"));
        StubPrPollData(runner, 42, "CHANGES_REQUESTED");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        result.Items.Single().PendingRevisions.ShouldBe(true);
        result.Summary.PlanOpen.ShouldBe(1);
        result.Summary.PendingRevisions.ShouldBe(1);
    }

    [Fact]
    public async Task PlannableRoot_MergedPr_Reports_Merged()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", "[]");
        StubPrList(runner, "merged", PrJson(42, "plan/100", mergedAtYear: 2026));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        var row = result.Items.Single();
        row.PlanStatus.ShouldBe("merged");
        row.PlanPrNumber.ShouldBe(42);
        row.PendingRevisions.ShouldBeNull();
    }

    [Fact]
    public async Task PlannableRoot_ClosedNotMergedPr_Reports_Abandoned()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", "[]");
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "closed", PrJson(42, "plan/100"));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        result.Items.Single().PlanStatus.ShouldBe("abandoned");
        result.Summary.PlanAbandoned.ShouldBe(1);
    }

    // ─── Mixed-tree / aggregation ─────────────────────────────────────

    [Fact]
    public async Task MixedTree_AggregatesAllStatuses()
    {
        // Tree:  100 (Epic, plannable) → 110 (Issue, plannable, open w/ CR)
        //                              → 120 (Issue, plannable, merged)
        //                              → 130 (Task, NOT plannable, → n/a)
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Apex").Build(),
            new WorkItemBuilder().WithId(110).WithType("Issue").WithParentId(100).WithTitle("Sub A").Build(),
            new WorkItemBuilder().WithId(120).WithType("Issue").WithParentId(100).WithTitle("Sub B").Build(),
            new WorkItemBuilder().WithId(130).WithType("Task").WithParentId(100).WithTitle("Leaf C").Build()
        );

        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        // Per-branch responders, registered before the catch-all.
        runner.When((exe, a) => exe == "gh" && a[0] == "pr" && a[1] == "list"
            && ContainsArg(a, "--head", "plan/100") && ContainsArg(a, "--state", "open"),
            new ProcessResult(0, "[]", ""));
        runner.When((exe, a) => exe == "gh" && a[0] == "pr" && a[1] == "list"
            && ContainsArg(a, "--head", "plan/100") && ContainsArg(a, "--state", "merged"),
            new ProcessResult(0, "[]", ""));
        runner.When((exe, a) => exe == "gh" && a[0] == "pr" && a[1] == "list"
            && ContainsArg(a, "--head", "plan/100") && ContainsArg(a, "--state", "closed"),
            new ProcessResult(0, "[]", ""));
        // 110: open with changes requested
        runner.When((exe, a) => exe == "gh" && a[0] == "pr" && a[1] == "list"
            && ContainsArg(a, "--head", "plan/100-110") && ContainsArg(a, "--state", "open"),
            new ProcessResult(0, PrJson(201, "plan/100-110"), ""));
        StubPrPollData(runner, 201, "CHANGES_REQUESTED");
        // 120: merged
        runner.When((exe, a) => exe == "gh" && a[0] == "pr" && a[1] == "list"
            && ContainsArg(a, "--head", "plan/100-120") && ContainsArg(a, "--state", "open"),
            new ProcessResult(0, "[]", ""));
        runner.When((exe, a) => exe == "gh" && a[0] == "pr" && a[1] == "list"
            && ContainsArg(a, "--head", "plan/100-120") && ContainsArg(a, "--state", "merged"),
            new ProcessResult(0, PrJson(202, "plan/100-120", mergedAtYear: 2026), ""));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        result.Success.ShouldBeTrue();

        // Default: --include-na false → 130 hidden from items.
        result.Items.Select(i => i.ItemId).ShouldBe(new[] { 100, 110, 120 });

        // Summary always covers full tree.
        result.Summary.TotalItems.ShouldBe(4);
        result.Summary.PlanNeeded.ShouldBe(1);     // 100
        result.Summary.PlanOpen.ShouldBe(1);       // 110
        result.Summary.PlanMerged.ShouldBe(1);     // 120
        result.Summary.PlanAbandoned.ShouldBe(0);
        result.Summary.PlanNa.ShouldBe(1);         // 130
        result.Summary.PendingRevisions.ShouldBe(1);
    }

    [Fact]
    public async Task IncludeNa_TogglesItemsArrayButNotSummary()
    {
        // 100 plannable+needed, 130 non-plannable → n/a.
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(130).WithType("Task").WithParentId(100).Build()
        );
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", "[]");
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "closed", "[]");

        var (_, hidden) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", includeNa: false, json: true));
        var (_, shown) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", includeNa: true, json: true));

        var hiddenResult = ParseJson(hidden);
        var shownResult = ParseJson(shown);

        hiddenResult.Items.Count.ShouldBe(1);
        hiddenResult.Items[0].ItemId.ShouldBe(100);
        shownResult.Items.Count.ShouldBe(2);

        // Both summaries should be identical.
        hiddenResult.Summary.TotalItems.ShouldBe(shownResult.Summary.TotalItems);
        hiddenResult.Summary.PlanNa.ShouldBe(1);
        shownResult.Summary.PlanNa.ShouldBe(1);
    }

    // ─── Slug / repo resolution ───────────────────────────────────────

    [Fact]
    public async Task ExplicitRepo_BypassesGitRemote()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var (cmd, runner) = CreateCommand();
        // No StubOrigin — would surface no_repo_slug if the verb tried git.
        runner.When((exe, a) => exe == "gh" && a[0] == "pr" && a[1] == "list",
            new ProcessResult(0, "[]", ""));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", repo: "owner/repo", json: true));
        var result = ParseJson(output);
        result.Success.ShouldBeTrue();
        result.Items.Single().PlanStatus.ShouldBe("needed");
    }

    [Fact]
    public async Task NoRepoSlug_NoRemote_EmitsError()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var (cmd, runner) = CreateCommand();
        // Stub git remote to fail.
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(128, "", "no remote"));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("no_repo_slug");
    }

    [Fact]
    public async Task NonPlannableTree_DoesNotRequireRepoSlug()
    {
        // All-Task tree must work even with no git remote configured —
        // n/a items don't need a slug because they don't query gh.
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Task").Build(),
            new WorkItemBuilder().WithId(110).WithType("Task").WithParentId(100).Build()
        );
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(128, "", ""));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", includeNa: true, json: true));
        var result = ParseJson(output);
        result.Success.ShouldBeTrue();
        result.Items.Count.ShouldBe(2);
    }

    // ─── gh failure propagation ───────────────────────────────────────

    [Fact]
    public async Task GhFailure_OnPrList_EmitsGhFailedEnvelope()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        runner.WhenAsync(
            (exe, a) => exe == "gh" && a[0] == "pr" && a[1] == "list",
            (_, _) => throw new InvalidOperationException("gh exploded"));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("gh_failed");
        result.ErrorMessage!.ShouldContain("plan/100");
    }

    // ─── Output formatting ────────────────────────────────────────────

    [Fact]
    public async Task HumanOutput_RendersTableWithSummary()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Apex").Build());
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", "[]");
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "closed", "[]");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: false));

        output.ShouldContain("plan status:");
        output.ShouldContain("root=100");
        output.ShouldContain("needed=1");
        output.ShouldContain("Apex");
        output.ShouldContain("ITEM");
    }

    [Fact]
    public async Task HumanOutput_OnError_RendersErrorLine()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: -1, manifest: "", json: false));
        output.ShouldContain("error:");
        output.ShouldContain("invalid_argument");
    }

    [Fact]
    public async Task JsonOutput_UsesSnakeCase()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Apex").Build());
        var (cmd, runner) = CreateCommand();
        StubOrigin(runner);
        StubPrList(runner, "open", "[]");
        StubPrList(runner, "merged", "[]");
        StubPrList(runner, "closed", "[]");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Status(root: 100, manifest: "", json: true));
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"plan_status\"");
        output.ShouldContain("\"total_items\"");
        output.ShouldContain("\"plan_n_a\""); // verifies the special-case naming
        output.ShouldContain("\"pending_revisions\"");
    }
}
