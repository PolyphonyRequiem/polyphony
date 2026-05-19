using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Round-trip tests for the <c>polyphony reset</c> verb family —
/// <c>reset state</c>, <c>reset prs</c>, <c>reset branches</c>,
/// <c>reset worktrees</c>, <c>reset manifest</c>, and the
/// <c>reset apex</c> composite.
///
/// <para>Mirrors the stubbing pattern from
/// <see cref="BranchCommandsMarkImplMergedTests"/>: real
/// <see cref="TwigClient"/> / <see cref="GitClient"/> / <see cref="GhClient"/>
/// over a <see cref="FakeProcessRunner"/>, so the verbs exercise the
/// same shell-out boundary they hit in production.</para>
/// </summary>
public sealed class ResetCommandsTests : CommandTestBase
{
    private (ResetCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var pullRequestReader = new PullRequestReader(gh, null);
        var resolver = new RepoIdentityResolver(git);
        var planObserver = new PlanObserver(git, gh, new ThrowingAdoClient(), twig, resolver);
        var walker = new HierarchyWalker(Config, Repository);

        var cmd = new ResetCommands(twig, git, pullRequestReader, planObserver, walker);
        return (cmd, runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    /// <summary>
    /// Stubs <c>twig show {id}</c> + <c>twig patch --id {id}</c> as a
    /// round-trip — mirrors the helper in
    /// BranchCommandsMarkImplMergedTests so reset state observes the
    /// patched tag through twig the same way production does.
    /// </summary>
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
                try
                {
                    using var doc = JsonDocument.Parse(args[4]);
                    if (doc.RootElement.TryGetProperty("System.Tags", out var tagsEl))
                    {
                        state[0] = tagsEl.GetString() ?? state[0];
                    }
                }
                catch (JsonException) { /* fall through */ }
                return Task.FromResult(new ProcessResult(0, "{}", ""));
            });
    }

    /// <summary>
    /// Stub <c>git remote get-url origin</c> + <c>git rev-parse</c> so
    /// PlanObserver.TryResolveRepoIdentityAsync resolves to a GitHub repo
    /// without needing a real working tree.
    /// </summary>
    private static void StubGitHubIdentity(FakeProcessRunner runner, string slug = "owner/repo")
    {
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, $"https://github.com/{slug}.git\n", ""));
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "C:/fake/.git\n", ""));
        runner.WhenExact("git", ["rev-parse", "--show-toplevel"],
            new ProcessResult(0, "C:/fake\n", ""));
    }

    // ---------- reset state -----------------------------------------------

    [Fact]
    public async Task ResetState_MissingApex_Halts()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(() => cmd.ResetState());
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task ResetState_DryRun_DoesNotInvokePatch()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetState(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetStateResult);
        result.ShouldNotBeNull();
        result.Apex.ShouldBe(100);
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeTrue();
        result.NewWatermark.ShouldNotBeNullOrEmpty();
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task ResetState_Execute_StampsWatermarkAndReportsPrevious()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100,
            "polyphony:root; polyphony:run-started-at=2024-01-01T00:00:00.000Z");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetState(apex: 100, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetStateResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeFalse();
        result.PreviousWatermark.ShouldStartWith("2024-01-01T00:00:00");
        // New watermark must be a parseable ISO-8601 instant.
        DateTimeOffset.Parse(result.NewWatermark!).ShouldBeGreaterThan(DateTimeOffset.MinValue);
        // Verify the patch was actually issued.
        runner.Invocations.ShouldContain(i =>
            i.Executable == "twig"
            && i.Arguments.Count > 0
            && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task ResetState_DuplicateTags_StripsAllButOne()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubTagsRoundTrip(runner, 100,
            "polyphony:root; polyphony:run-started-at=2024-01-01T00:00:00.000Z; polyphony:run-started-at=2024-02-01T00:00:00.000Z");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetState(apex: 100, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetStateResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        // Two original tags → strip them all, leave one fresh. Duplicates removed = total - 1.
        result.RemovedDuplicateTags.ShouldBe(1);
    }

    // ---------- reset branches --------------------------------------------

    [Fact]
    public async Task ResetBranches_DryRun_DoesNotInvokeDelete()
    {
        var (cmd, runner) = CreateCommand();
        // Stub ls-remote to return one match for plan/100.
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 3 && a[0] == "ls-remote" && a[1] == "--heads",
            (a, _) =>
            {
                // a[2]="origin", a[3]=pattern.
                var pattern = a.Count > 3 ? a[3] : string.Empty;
                if (pattern == "refs/heads/plan/100")
                {
                    return Task.FromResult(new ProcessResult(0,
                        "deadbeef\trefs/heads/plan/100\n", ""));
                }
                return Task.FromResult(new ProcessResult(0, "", ""));
            });
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "for-each-ref",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetBranches(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetBranchesResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeTrue();
        result.DeletedBranches.Count.ShouldBe(1);
        result.DeletedBranches[0].Branch.ShouldBe("plan/100");
        result.DeletedBranches[0].DeletedRemote.ShouldBeTrue();
        // No actual delete commands.
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 1
            && (i.Arguments[0] == "branch" || (i.Arguments[0] == "push" && i.Arguments.Contains("--delete"))));
    }

    /// <summary>
    /// Regression for the MG branch-pattern delimiter bug: the apex
    /// branch-pattern table must use `_` between root_id and mg_path
    /// (per docs/decisions/branch-model.md §Branch names —
    /// `mg/{root_id}_{mg_path}`), not `-`. An earlier revision used `-`,
    /// which silently failed to match any real MG branch and left
    /// mg/* refs on origin after reset — so apex redispatch detected
    /// the stale merged MG state and short-circuited the run.
    /// </summary>
    [Fact]
    public async Task ResetBranches_DiscoversMgBranchesWithUnderscoreDelimiter()
    {
        var (cmd, runner) = CreateCommand();

        // Stub ls-remote to mimic an apex (root_id=100) with the canonical
        // branch shapes: plan/, feature/, mg/{root}_{mg_path}, impl/{root}-{item}.
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 4 && a[0] == "ls-remote" && a[1] == "--heads",
            (a, _) =>
            {
                var pattern = a[3];
                return pattern switch
                {
                    "refs/heads/plan/100" => Task.FromResult(new ProcessResult(0,
                        "sha\trefs/heads/plan/100\n", "")),
                    "refs/heads/feature/100" => Task.FromResult(new ProcessResult(0,
                        "sha\trefs/heads/feature/100\n", "")),
                    "refs/heads/mg/100_*" => Task.FromResult(new ProcessResult(0,
                        "sha\trefs/heads/mg/100_pg-100\nsha\trefs/heads/mg/100_data-layer_migrations\n", "")),
                    "refs/heads/impl/100-*" => Task.FromResult(new ProcessResult(0,
                        "sha\trefs/heads/impl/100-100\n", "")),
                    _ => Task.FromResult(new ProcessResult(0, "", "")),
                };
            });
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "for-each-ref",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetBranches(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetBranchesResult);
        result.ShouldNotBeNull();
        var branches = result.DeletedBranches.Select(b => b.Branch).ToList();
        branches.ShouldContain("mg/100_pg-100",
            customMessage: "top-level MG branch (mg/{root_id}_{mg_id}) must be discovered by the apex-pattern enumerator");
        branches.ShouldContain("mg/100_data-layer_migrations",
            customMessage: "nested MG branch (mg/{root_id}_{parent_mg_id}_{nested_mg_id}) must be discovered too");
    }

    // ---------- reset worktrees -------------------------------------------

    [Fact]
    public async Task ResetWorktrees_NoWorktreesUnderApex_SucceedsWithEmptyList()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "C:/repo/.git\n", ""));
        // No worktrees at all → filter yields empty regardless of root path.
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetWorktrees(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetWorktreesResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeTrue();
        result.RemovedWorktrees.Count.ShouldBe(0);
        result.ApexRunsRoot.ShouldEndWith("apex-100");
    }

    // ---------- reset manifest --------------------------------------------

    [Fact]
    public async Task ResetManifest_FeatureBranchAbsent_ReportsAbsent()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "ls-remote",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetManifest(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetManifestResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.FeatureBranch.ShouldBe("feature/100");
        result.FeatureBranchExists.ShouldBeFalse();
        result.ManifestPresent.ShouldBeFalse();
        result.DeferralReason.ShouldNotBeNullOrEmpty();
    }

    // ---------- reset prs -------------------------------------------------

    [Fact]
    public async Task ResetPrs_NoOpenPrs_ReportsSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubGitHubIdentity(runner);
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "ls-remote",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetPrs(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetPrsResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.AbandonedPrs.Count.ShouldBe(0);
        result.FailedPrs.Count.ShouldBe(0);
    }

    // ---------- reset apex composite --------------------------------------

    [Fact]
    public async Task ResetApex_SkipState_OmitsStateStep()
    {
        var (cmd, runner) = CreateCommand();
        StubGitHubIdentity(runner);
        // facets step walks the apex via the hierarchy walker (real
        // SqliteWorkItemRepository); seed the apex so the walk finds it.
        await SeedAsync(new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Apex").WithState("To Do").Build());
        StubSync(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root");
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "ls-remote",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "for-each-ref",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "C:/repo/.git\n", ""));
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetApex(apex: 100, execute: false, skipState: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetApexResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.StateSkipped.ShouldBeTrue();
        result.StepsCompleted.ShouldNotContain("state");
        result.State.ShouldBeNull();
        // Other steps ran in order.
        result.StepsCompleted.ShouldContain("prs");
        result.StepsCompleted.ShouldContain("worktrees");
        result.StepsCompleted.ShouldContain("branches");
        result.StepsCompleted.ShouldContain("facets");
        result.StepsCompleted.ShouldContain("manifest");
        // Composite is dry-run end-to-end.
        result.DryRun.ShouldBeTrue();
        result.Prs!.DryRun.ShouldBeTrue();
        result.Worktrees!.DryRun.ShouldBeTrue();
        result.Branches!.DryRun.ShouldBeTrue();
        result.Facets!.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task ResetApex_FacetsRunsBetweenBranchesAndManifest()
    {
        var (cmd, runner) = CreateCommand();
        StubGitHubIdentity(runner);
        await SeedAsync(new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Apex").WithState("To Do").Build());
        StubSync(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root");
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "ls-remote",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "for-each-ref",
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "C:/repo/.git\n", ""));
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetApex(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetApexResult);
        result.ShouldNotBeNull();
        // facets must appear after branches and before manifest in the
        // completed-steps order — guarantees the chain documented in
        // ResetApexResult is what the composite actually runs.
        var idxBranches = result.StepsCompleted.ToList().IndexOf("branches");
        var idxFacets = result.StepsCompleted.ToList().IndexOf("facets");
        var idxManifest = result.StepsCompleted.ToList().IndexOf("manifest");
        idxBranches.ShouldBeGreaterThanOrEqualTo(0);
        idxFacets.ShouldBeGreaterThan(idxBranches);
        idxManifest.ShouldBeGreaterThan(idxFacets);
        result.Facets.ShouldNotBeNull();
        result.Facets.Apex.ShouldBe(100);
    }

    // ---------- reset facets ----------------------------------------------

    [Fact]
    public async Task ResetFacets_MissingApex_Halts()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(() => cmd.ResetFacets());
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task ResetFacets_ApexNotInCache_ReportsErrorWithoutPatching()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        // Do NOT seed the apex — walker.WalkAsync returns null.

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetFacets(apex: 999, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetFacetsResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("999");
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task ResetFacets_NoTargetedTags_ReportsSuccessWithZeroModifications()
    {
        var (cmd, runner) = CreateCommand();
        await SeedAsync(new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Apex").WithState("To Do")
            .WithTags("polyphony:root")
            .Build());
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetFacets(apex: 100, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetFacetsResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.ItemsScanned.ShouldBe(1);
        result.ItemsModified.ShouldBe(0);
        result.Items.Count.ShouldBe(0);
        // Nothing to remove → no patch issued.
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task ResetFacets_DryRun_ReportsRemovalsButDoesNotPatch()
    {
        var (cmd, runner) = CreateCommand();
        await SeedAsync(new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Apex").WithState("To Do")
            .WithTags("polyphony:root; polyphony:facets=implementable; polyphony:planned")
            .Build());
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetFacets(apex: 100, execute: false));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetFacetsResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeTrue();
        result.ItemsScanned.ShouldBe(1);
        result.ItemsModified.ShouldBe(1);
        result.TotalFacetTagsRemoved.ShouldBe(1);
        result.TotalPlannedTagsRemoved.ShouldBe(1);
        result.Items.Count.ShouldBe(1);
        result.Items[0].WorkItemId.ShouldBe(100);
        result.Items[0].PlannedTagRemoved.ShouldBeTrue();
        result.Items[0].FacetTagsRemoved.Count.ShouldBe(1);
        result.Items[0].FacetTagsRemoved[0].ShouldStartWith("polyphony:facets=");
        result.Items[0].Verified.ShouldBeNull(); // null in dry-run
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task ResetFacets_Execute_RemovesFacetsAndPlannedTagsAndVerifies()
    {
        var (cmd, runner) = CreateCommand();
        await SeedAsync(new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Apex").WithState("To Do")
            .WithTags("polyphony:root; polyphony:facets=implementable; polyphony:planned")
            .Build());
        StubSync(runner);
        StubTagsRoundTrip(runner, 100,
            "polyphony:root; polyphony:facets=implementable; polyphony:planned");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetFacets(apex: 100, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetFacetsResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeFalse();
        result.ItemsModified.ShouldBe(1);
        result.TotalFacetTagsRemoved.ShouldBe(1);
        result.TotalPlannedTagsRemoved.ShouldBe(1);
        result.Items[0].Verified.ShouldBe(true);
        result.Items[0].Error.ShouldBeNull();
        // Patch was issued with the surviving tag only.
        var patches = runner.Invocations.Where(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch").ToList();
        patches.ShouldNotBeEmpty();
        var json = patches[0].Arguments[4];
        json.ShouldContain("polyphony:root");
        json.ShouldNotContain("polyphony:facets=");
        json.ShouldNotContain("polyphony:planned");
    }

    [Fact]
    public async Task ResetFacets_Execute_WalksDescendantsAndPatchesEach()
    {
        var (cmd, runner) = CreateCommand();
        // Apex has a child plannable parent that ALSO carries a facets
        // override. Both must be cleaned in a single walk.
        var apex = new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Apex").WithState("To Do")
            .WithTags("polyphony:root; polyphony:facets=implementable")
            .Build();
        var child = new WorkItemBuilder()
            .WithId(101).WithType("Issue").WithTitle("Child").WithState("To Do")
            .WithParentId(100)
            .WithTags("polyphony:planned")
            .Build();
        await SeedAsync(apex, child);
        StubSync(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root; polyphony:facets=implementable");
        StubTagsRoundTrip(runner, 101, "polyphony:planned");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetFacets(apex: 100, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetFacetsResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.ItemsScanned.ShouldBe(2);
        result.ItemsModified.ShouldBe(2);
        result.TotalFacetTagsRemoved.ShouldBe(1);
        result.TotalPlannedTagsRemoved.ShouldBe(1);
        // Both items received a patch.
        var patchedIds = runner.Invocations
            .Where(i => i.Executable == "twig" && i.Arguments.Count >= 3
                && i.Arguments[0] == "patch" && i.Arguments[1] == "--id")
            .Select(i => i.Arguments[2])
            .ToHashSet();
        patchedIds.ShouldContain("100");
        patchedIds.ShouldContain("101");
    }

    [Fact]
    public async Task ResetFacets_VerifyFailsOnSilentRevert_ReportsPerItemError()
    {
        var (cmd, runner) = CreateCommand();
        await SeedAsync(new WorkItemBuilder()
            .WithId(100).WithType("Issue").WithTitle("Apex").WithState("To Do")
            .WithTags("polyphony:root; polyphony:facets=implementable")
            .Build());
        StubSync(runner);
        // Stub `twig show` to ALWAYS return the original tags — even
        // after the patch lands. Simulates an ADO eventual-consistency
        // revert: patch + sync exit 0 but the cache still reports the
        // pre-patch state. Read-after-write check must catch this.
        runner.WhenAsync(
            (e, a) => e == "twig" && a.Count >= 4 && a[0] == "show"
                && a[1] == "100" && a[^1] == "json",
            (_, _) => Task.FromResult(new ProcessResult(0,
                """{"id":100,"tags":"polyphony:root; polyphony:facets=implementable"}""", "")));
        runner.WhenAsync(
            (e, a) => e == "twig" && a.Count >= 2 && a[0] == "patch" && a[1] == "--id",
            (_, _) => Task.FromResult(new ProcessResult(0, "{}", "")));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ResetFacets(apex: 100, execute: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetFacetsResult);
        result.ShouldNotBeNull();
        // Verb-wide success stays true (the walk completed) but the
        // per-item entry must surface the verification failure.
        result.Success.ShouldBeTrue();
        result.Items.Count.ShouldBe(1);
        result.Items[0].Verified.ShouldBe(false);
        result.Items[0].Error!.ShouldContain("assertion failed");
        // ItemsModified is the count of SUCCESSFUL modifications, so 0.
        result.ItemsModified.ShouldBe(0);
    }
}
