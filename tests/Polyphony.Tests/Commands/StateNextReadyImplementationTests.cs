using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Integration tests for <see cref="StateCommands.NextReady"/> after
/// closed-loop PR #4: <see cref="PlanObserver.MapImplementationMerged"/>
/// drives the <c>implementation_merged</c> disposition from live impl-PR
/// state (<c>impl/{root}-{item}</c>) instead of the (broken) item.State +
/// child.State heuristic.
/// </summary>
/// <remarks>
/// <para>
/// Pre-PR-#4 the verb derived <c>implementation_merged</c> from
/// <c>(item.State, childCount, allChildrenDone)</c> alone — no PR
/// introspection at all, no closed loop after merge. The fix wires the
/// canonical impl branch (<c>impl/{root}-{item}</c> per
/// <c>BranchNameBuilder.Impl</c>) through <c>gh pr list</c> +
/// <c>gh pr view</c> into the observation scope.
/// </para>
/// <para>
/// Mocks the <c>git remote get-url</c>, <c>git ls-remote</c>,
/// <c>gh pr list</c>, and <c>gh pr view</c> shell-outs through
/// <see cref="FakeProcessRunner"/> — same pattern as
/// <see cref="StateNextReadyChildrenSeededTests"/> shipped in PR #3.
/// Plan-kind shell-outs are stubbed to "no signal" so they do not
/// confound the implementation_merged assertions.
/// </para>
/// </remarks>
public sealed class StateNextReadyImplementationTests : CommandTestBase
{
    private const int ApexId = 3043;
    private const string PlanBranch = "plan/3043";
    private const string ImplBranch = "impl/3043-3043";
    private const string OriginUrl = "https://github.com/acme/repo.git";

    private StateCommands CreateCommand(FakeProcessRunner runner, ProcessConfig? configOverride = null)
    {
        var config = configOverride ?? Config;
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var planObserver = new PlanObserver(git, gh, new ThrowingAdoClient(), twig, new RepoIdentityResolver(git));
        return new StateCommands(twig, git, gh, runner, Repository, config, planObserver);
    }

    /// <summary>Configure the runner with a minimal "no signal" baseline
    /// for plan-kind I/O (origin remote resolves, plan ls-remote shows no
    /// branch, plan-branch <c>gh pr list</c> returns []) and a default
    /// "no impl PR" stub. Caller-provided impl-PR responders MUST be
    /// registered <em>before</em> calling this so they win the
    /// first-match dispatch in <see cref="FakeProcessRunner"/>.</summary>
    private static FakeProcessRunner NewRunnerWithBaseline()
    {
        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, OriginUrl + "\n", ""));
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{PlanBranch}"],
            new ProcessResult(0, "", ""));
        // Twig show: no planned tag (children_seeded → Needed). Tests that
        // care about the planned tag stub explicitly above this baseline.
        runner.WhenStartsWith("twig", ["show"], new ProcessResult(0,
            $$"""{"id":{{ApexId}},"title":"Apex","tags":""}""", ""));
        // Catch-all gh pr list — last responder so impl/plan-specific
        // matchers registered earlier (via StubImplPr*) can win.
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
        return runner;
    }

    /// <summary>Stub <c>gh pr list --head {ImplBranch}</c> with a single
    /// PR. Must be registered BEFORE <see cref="NewRunnerWithBaseline"/>'s
    /// catch-all so the matcher wins. <see cref="GhClient.BuildPrListArgs"/>
    /// emits <c>--head</c> followed by the branch name as separate args;
    /// matching on consecutive <c>--head impl/...</c> identifies the
    /// impl-branch query unambiguously.</summary>
    private static void StubImplPrList(FakeProcessRunner runner, int prNumber)
        => runner.When(
            (e, a) => e == "gh"
                && a.Count >= 4 && a[0] == "pr" && a[1] == "list"
                && HasHeadFilter(a, ImplBranch),
            new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"headRefName":"{{ImplBranch}}","url":"https://gh/pr/{{prNumber}}"}]""",
                ""));

    /// <summary>Stub <c>gh pr list --head {ImplBranch}</c> with a
    /// gh-failure response. Same registration ordering as
    /// <see cref="StubImplPrList"/>.</summary>
    private static void StubImplPrListFails(FakeProcessRunner runner)
        => runner.When(
            (e, a) => e == "gh"
                && a.Count >= 4 && a[0] == "pr" && a[1] == "list"
                && HasHeadFilter(a, ImplBranch),
            new ProcessResult(1, "", "gh: API rate limit exceeded"));

    private static void StubImplPrPoll(
        FakeProcessRunner runner,
        int prNumber,
        string state)
    {
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "",
              "mergeable": "MERGEABLE",
              "headRefName": "{{ImplBranch}}",
              "headRefOid": "abc123",
              "baseRefName": "mg/3043_root",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private static bool HasHeadFilter(IReadOnlyList<string> args, string branch)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--head" && args[i + 1] == branch) return true;
        }
        return false;
    }

    private async Task SeedApexAsync(string state = "Doing")
    {
        var item = new WorkItemBuilder()
            .WithId(ApexId).WithType("Issue").WithTitle("Apex 3043").WithState(state).Build();
        await SeedAsync(item);
    }

    private async Task SeedApexWithChildrenAsync(int childCount, string childState = "To Do")
    {
        var apex = new WorkItemBuilder()
            .WithId(ApexId).WithType("Issue").WithTitle("Apex 3043").WithState("Doing").Build();
        var children = new List<Twig.Domain.Aggregates.WorkItem> { apex };
        for (var i = 0; i < childCount; i++)
        {
            children.Add(new WorkItemBuilder()
                .WithId(ApexId + 100 + i).WithType("Task").WithTitle($"Child {i}").WithState(childState)
                .WithParentId(ApexId).Build());
        }
        await SeedAsync(children.ToArray());
    }

    // ─── No impl branch on origin / no PR → Needed ─────────────────────

    [Fact]
    public async Task NextReady_NoImplPr_ImplementationMergedNeeded_WithReason()
    {
        // Apex 3043 reproduction: the impl branch has not been opened yet
        // (no PR exists for impl/3043-3043). Pre-PR-#4 the verb returned
        // Needed via the (item.State="Doing", childCount=0) fall-through
        // arm of the legacy switch — same answer for the wrong reason
        // (no PR introspection at all).
        await SeedApexAsync();
        var runner = NewRunnerWithBaseline();

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ImplementationMerged);
        result.Fulfilling.ShouldNotContain(RequirementKind.ImplementationMerged);
        result.Needed.ShouldContain(RequirementKind.ImplementationMerged);

        result.ObservationReasons.ShouldNotBeNull();
        result.ObservationReasons!.ShouldContainKey(RequirementKind.ImplementationMerged);
        result.ObservationReasons![RequirementKind.ImplementationMerged]
            .ShouldContain(ImplBranch);
        result.ObservationReasons![RequirementKind.ImplementationMerged]
            .ShouldContain("no impl PR");
    }

    // ─── Open impl PR → Fulfilling ─────────────────────────────────────

    [Fact]
    public async Task NextReady_OpenImplPr_ImplementationMergedFulfilling()
    {
        await SeedApexAsync();
        var runner = new FakeProcessRunner();
        StubImplPrList(runner, prNumber: 412);
        StubImplPrPoll(runner, prNumber: 412, state: "OPEN");
        // Baseline registers the catch-all PR-list AFTER the impl-specific
        // one so impl wins the first-match dispatch.
        BindBaselineAfterImpl(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Fulfilling.ShouldContain(RequirementKind.ImplementationMerged);
        result.Satisfied.ShouldNotContain(RequirementKind.ImplementationMerged);
        result.Needed.ShouldNotContain(RequirementKind.ImplementationMerged);

        result.ObservationReasons!.ShouldContainKey(RequirementKind.ImplementationMerged);
        result.ObservationReasons![RequirementKind.ImplementationMerged].ShouldContain("412");
        result.ObservationReasons![RequirementKind.ImplementationMerged].ShouldContain("open");
    }

    // ─── Merged impl PR → Satisfied ────────────────────────────────────

    [Fact]
    public async Task NextReady_MergedImplPr_ImplementationMergedSatisfied()
    {
        await SeedApexAsync();
        var runner = new FakeProcessRunner();
        StubImplPrList(runner, prNumber: 412);
        StubImplPrPoll(runner, prNumber: 412, state: "MERGED");
        BindBaselineAfterImpl(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.ImplementationMerged);
        result.Needed.ShouldNotContain(RequirementKind.ImplementationMerged);
        result.Fulfilling.ShouldNotContain(RequirementKind.ImplementationMerged);

        result.ObservationReasons!.ShouldContainKey(RequirementKind.ImplementationMerged);
        result.ObservationReasons![RequirementKind.ImplementationMerged].ShouldContain("merged");
    }

    // ─── Failed gh pr list → Needed with diagnostic, no throw ──────────

    [Fact]
    public async Task NextReady_GhPrListFails_ImplementationMergedNeeded_NoExceptionEscapes()
    {
        // gh pr list returns non-zero → GhClient surfaces an empty list
        // (not an exception) so the FetchImplPrAsync try/catch actually
        // sees "no PR" rather than a throw. Verify the verb still exits
        // 0 with implementation_merged Needed and a non-empty reason.
        // The throw-path (real ExternalToolException) is exercised by
        // the GhPrListThrows test below.
        await SeedApexAsync();
        var runner = new FakeProcessRunner();
        StubImplPrListFails(runner);
        BindBaselineAfterImpl(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Needed.ShouldContain(RequirementKind.ImplementationMerged);
        result.Satisfied.ShouldNotContain(RequirementKind.ImplementationMerged);
        result.ObservationReasons!.ShouldContainKey(RequirementKind.ImplementationMerged);
        result.ObservationReasons![RequirementKind.ImplementationMerged].ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task NextReady_GhPrListThrows_ImplementationMergedNeeded_NoExceptionEscapes()
    {
        // Defensive layer: even when the underlying gh process throws
        // (e.g. ExternalToolException for a broken pipe), FetchImplPrAsync
        // must catch and surface the error via ImplPrFetchError → Needed
        // with a non-empty reason. The verb's "always exit 0" contract
        // holds.
        await SeedApexAsync();
        var runner = new FakeProcessRunner();
        runner.WhenAsync(
            (e, a) => e == "gh"
                && a.Count >= 4 && a[0] == "pr" && a[1] == "list"
                && HasHeadFilter(a, ImplBranch),
            (_, _) => throw new ExternalToolException(
                "gh", ["pr", "list"], 1, "", "broken pipe"));
        BindBaselineAfterImpl(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Needed.ShouldContain(RequirementKind.ImplementationMerged);
        result.ObservationReasons!.ShouldContainKey(RequirementKind.ImplementationMerged);
        result.ObservationReasons![RequirementKind.ImplementationMerged]
            .ShouldNotBeNullOrWhiteSpace();
        result.ObservationReasons![RequirementKind.ImplementationMerged]
            .ShouldContain("gh pr list", Case.Sensitive);
    }

    // ─── MG roll-up case (PR #5) ───────────────────────────────────────

    [Fact]
    public async Task NextReady_MgItemSelfPrMerged_ChildrenUnmerged_DemotedByChildren()
    {
        // PR #5 cross-item rollup: an MG-style item (apex with
        // implementable children) whose own impl PR (impl/3043-3043)
        // is merged is NOT Satisfied for implementation_merged when
        // any child impl PR is unmerged or absent. The composer takes
        // the worst-of (parent self, every implementable child) →
        // children with no impl PR drop the joint disposition to
        // Needed (closed-loop §3.1 row 5). The reason string surfaces
        // the offending child's id so callers can drill down without
        // re-deriving the rollup themselves.
        await SeedApexWithChildrenAsync(childCount: 3);
        var runner = new FakeProcessRunner();
        StubImplPrList(runner, prNumber: 412);
        StubImplPrPoll(runner, prNumber: 412, state: "MERGED");
        BindBaselineAfterImpl(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // Children have no impl PR (catch-all gh pr list returns []),
        // so each child's reduced impl_merged is Needed → worst-of
        // demotes the parent to Needed even though its own impl PR
        // merged.
        result.Needed.ShouldContain(RequirementKind.ImplementationMerged);

        result.ObservationReasons!.ShouldContainKey(RequirementKind.ImplementationMerged);
        var reason = result.ObservationReasons![RequirementKind.ImplementationMerged];
        reason.ShouldContain("merged");
        reason.ShouldContain("child #");
    }

    [Fact]
    public async Task NextReady_MgWithTwoChildren_AllImplMerged_ImplementationMergedSatisfied()
    {
        // Closed-loop §3.1 row 5 happy path: MG-style apex with two
        // implementable children, all three impl PRs merged → parent's
        // worst-of stays at Satisfied → observed flows through the
        // reducer untouched and result.Satisfied carries
        // implementation_merged. Pair to the existing
        // ChildrenUnmerged_DemotedByChildren test (which validates the
        // demotion direction) with N=2.
        await SeedApexWithChildrenAsync(childCount: 2);
        var runner = new FakeProcessRunner();
        StubImplPrList(runner, prNumber: 412);
        StubImplPrPoll(runner, prNumber: 412, state: "MERGED");
        StubChildImplPrMerged(runner, childId: ApexId + 100, prNumber: 510);
        StubChildImplPrMerged(runner, childId: ApexId + 101, prNumber: 511);
        BindBaselineAfterImpl(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.ImplementationMerged);
        result.Needed.ShouldNotContain(RequirementKind.ImplementationMerged);
        result.Fulfilling.ShouldNotContain(RequirementKind.ImplementationMerged);

        var reason = result.ObservationReasons![RequirementKind.ImplementationMerged];
        reason.ShouldContain("merged");
        // No child # marker because no child weakened the disposition —
        // the rolled-up reason is the parent's own self reason verbatim.
        reason.ShouldNotContain("child #");
    }

    [Fact]
    public async Task NextReady_MgWithTwoChildren_OneImplOpen_ImplementationMergedNotSatisfied_CitesOpenChild()
    {
        // PR #5 mixed-children path with N=2 (the existing
        // DemotedByChildren test only exercised "all unmerged"). Parent
        // self merged + one child merged + one child open: worst-of
        // produces Fulfilling (the Order rank for OPEN in
        // MapImplementationMerged). The exact downgraded disposition
        // is implementation detail — what callers depend on is "not
        // Satisfied" + a structured pointer to the offending child.
        await SeedApexWithChildrenAsync(childCount: 2);
        var (mergedChildId, openChildId) = (ApexId + 100, ApexId + 101);
        var runner = new FakeProcessRunner();
        StubImplPrList(runner, prNumber: 412);
        StubImplPrPoll(runner, prNumber: 412, state: "MERGED");
        StubChildImplPrMerged(runner, childId: mergedChildId, prNumber: 510);
        StubChildImplPrOpen(runner, childId: openChildId, prNumber: 511);
        BindBaselineAfterImpl(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ImplementationMerged);

        var reason = result.ObservationReasons![RequirementKind.ImplementationMerged];
        reason.ShouldContain($"child #{openChildId}");
        reason.ShouldNotContain($"child #{mergedChildId}");
    }

    /// <summary>Stub a merged impl PR for one specific child id. The
    /// canonical impl branch for a child whose root is
    /// <see cref="ApexId"/> is <c>impl/{ApexId}-{childId}</c>; the head
    /// filter disambiguates from the apex's own
    /// <c>impl/{ApexId}-{ApexId}</c> matcher and from the catch-all.</summary>
    private static void StubChildImplPrMerged(FakeProcessRunner runner, int childId, int prNumber)
    {
        var branch = $"impl/{ApexId}-{childId}";
        runner.When(
            (e, a) => e == "gh"
                && a.Count >= 4 && a[0] == "pr" && a[1] == "list"
                && HasHeadFilter(a, branch),
            new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"headRefName":"{{branch}}","url":"https://gh/pr/{{prNumber}}"}]""",
                ""));
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(0, ChildImplPrPollJson(prNumber, "MERGED", branch), ""));
    }

    /// <summary>Stub an open impl PR for one specific child id — sister
    /// helper to <see cref="StubChildImplPrMerged"/>.</summary>
    private static void StubChildImplPrOpen(FakeProcessRunner runner, int childId, int prNumber)
    {
        var branch = $"impl/{ApexId}-{childId}";
        runner.When(
            (e, a) => e == "gh"
                && a.Count >= 4 && a[0] == "pr" && a[1] == "list"
                && HasHeadFilter(a, branch),
            new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"headRefName":"{{branch}}","url":"https://gh/pr/{{prNumber}}"}]""",
                ""));
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(0, ChildImplPrPollJson(prNumber, "OPEN", branch), ""));
    }

    private static string ChildImplPrPollJson(int prNumber, string state, string branch) => $$"""
        {
          "number": {{prNumber}},
          "state": "{{state}}",
          "reviewDecision": "",
          "mergeable": "MERGEABLE",
          "headRefName": "{{branch}}",
          "headRefOid": "abc123",
          "baseRefName": "mg/{{ApexId}}_root",
          "mergedAt": null,
          "mergeCommit": null,
          "body": "",
          "reviews": []
        }
        """;

    /// <summary>Register the plan-kind / twig baseline AFTER an impl-PR
    /// matcher so the impl-specific responder wins the first-match
    /// dispatch in <see cref="FakeProcessRunner"/>. Must be called as the
    /// last setup step on a runner that already has impl matchers in
    /// place.</summary>
    private static void BindBaselineAfterImpl(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, OriginUrl + "\n", ""));
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{PlanBranch}"],
            new ProcessResult(0, "", ""));
        runner.WhenStartsWith("twig", ["show"], new ProcessResult(0,
            $$"""{"id":{{ApexId}},"title":"Apex","tags":""}""", ""));
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
    }
}
