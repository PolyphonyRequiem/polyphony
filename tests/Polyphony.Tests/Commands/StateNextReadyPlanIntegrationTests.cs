using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Integration tests for <see cref="StateCommands.NextReady"/> after
/// closed-loop PR #2: <see cref="PlanObserver"/> drives the
/// <c>plan_authored</c>, <c>plan_reviewed</c>, and <c>plan_promoted</c>
/// dispositions from live PR state instead of the (broken) <c>*.plan.md</c>
/// filesystem glob.
/// </summary>
/// <remarks>
/// Mocks the <c>git remote get-url</c>, <c>git ls-remote</c>,
/// <c>gh pr list</c>, and <c>gh pr view</c> shell-outs through
/// <see cref="FakeProcessRunner"/> — same pattern as
/// <see cref="Tests.Sdlc.Observers.PlanObserverTests"/> shipped in PR #1.
/// </remarks>
public sealed class StateNextReadyPlanIntegrationTests : CommandTestBase
{
    private const int ApexId = 3043;
    private const string PlanBranch = "plan/3043";
    private const string OriginUrl = "https://github.com/acme/repo.git";

    private static FakeProcessRunner NewRunnerWithRemote()
    {
        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, OriginUrl + "\n", ""));
        return runner;
    }

    private StateCommands CreateCommand(FakeProcessRunner runner, ProcessConfig? configOverride = null)
    {
        var config = configOverride ?? Config;
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var planObserver = new PlanObserver(git, gh, twig);
        return new StateCommands(twig, git, gh, runner, Repository, config, planObserver);
    }

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListSingle(FakeProcessRunner runner, int prNumber, string headRef)
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"headRefName":"{{headRef}}","url":"https://gh/pr/{{prNumber}}"}]""",
                ""));

    private static void StubPrPoll(
        FakeProcessRunner runner,
        int prNumber,
        string state,
        string headRef,
        string reviewDecision = "REVIEW_REQUIRED")
    {
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "{{reviewDecision}}",
              "mergeable": "MERGEABLE",
              "headRefName": "{{headRef}}",
              "headRefOid": "abc123",
              "baseRefName": "feature/3043",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private async Task SeedApexAsync()
    {
        var item = new WorkItemBuilder()
            .WithId(ApexId).WithType("Issue").WithTitle("Apex 3043").WithState("Doing").Build();
        await SeedAsync(item);
    }

    // ─── Plan-authored: no plan branch ──────────────────────────────────

    [Fact]
    public async Task NextReady_NoPlanBranch_PlanAuthored_Needed_WithReason()
    {
        await SeedApexAsync();
        var runner = NewRunnerWithRemote();
        StubLsRemote(runner, PlanBranch, exists: false);
        StubPrListEmpty(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // plan_authored is the entry in the plan-kind chain — with no
        // prerequisites it is promoted from observed Needed to reducer-derived
        // Ready (and thus appears in Next, not Needed). plan_reviewed and
        // plan_promoted depend on plan_authored being Satisfied first, so
        // they stay Needed.
        result.Next.ShouldContain(RequirementKind.PlanAuthored);
        result.Needed.ShouldContain(RequirementKind.PlanReviewed);
        result.Needed.ShouldContain(RequirementKind.PlanPromoted);

        result.ObservationReasons.ShouldNotBeNull();
        result.ObservationReasons!.ShouldContainKey(RequirementKind.PlanAuthored);
        result.ObservationReasons[RequirementKind.PlanAuthored].ShouldNotBeNullOrWhiteSpace();
        result.ObservationReasons[RequirementKind.PlanAuthored].ShouldContain("no plan branch");
    }

    // ─── Plan-authored: open plan PR (Fulfilling) ───────────────────────

    [Fact]
    public async Task NextReady_OpenPlanPr_AllPlanKinds_Fulfilling()
    {
        await SeedApexAsync();
        var runner = NewRunnerWithRemote();
        StubLsRemote(runner, PlanBranch, exists: true);
        StubPrListSingle(runner, 204, PlanBranch);
        StubPrPoll(runner, 204, state: "OPEN", headRef: PlanBranch, reviewDecision: "REVIEW_REQUIRED");

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // OPEN PR + REVIEW_REQUIRED:
        //   plan_authored Fulfilling (PR exists, not merged)
        //   plan_reviewed Fulfilling (review pending)
        //   plan_promoted Fulfilling (awaiting merge)
        result.Fulfilling.ShouldContain(RequirementKind.PlanAuthored);
        result.Fulfilling.ShouldContain(RequirementKind.PlanReviewed);
        result.Fulfilling.ShouldContain(RequirementKind.PlanPromoted);
        result.Satisfied.ShouldNotContain(RequirementKind.PlanAuthored);

        result.ObservationReasons.ShouldNotBeNull();
        result.ObservationReasons![RequirementKind.PlanAuthored].ShouldContain("204");
        result.ObservationReasons[RequirementKind.PlanAuthored].ShouldContain("open");
    }

    // ─── Smoking-gun #3043: merged plan PR → all 3 Satisfied ────────────

    [Fact]
    public async Task NextReady_MergedPlanPr_AllPlanKinds_Satisfied_FixesSmokingGun()
    {
        // Reproduces the apex 3043 smoking-gun from
        // files/closed-loop-state-plan.md §2: plan PR merged → all three
        // plan-kind requirements should be Satisfied. Pre-PR-#2 the verb
        // returned all three as Needed because *.plan.md no longer exists.
        await SeedApexAsync();
        var runner = NewRunnerWithRemote();
        StubLsRemote(runner, PlanBranch, exists: true);
        StubPrListSingle(runner, 204, PlanBranch);
        StubPrPoll(runner, 204, state: "MERGED", headRef: PlanBranch, reviewDecision: "APPROVED");

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.PlanAuthored);
        result.Satisfied.ShouldContain(RequirementKind.PlanReviewed);
        result.Satisfied.ShouldContain(RequirementKind.PlanPromoted);

        // Regression guard: the pre-PR-#2 verb returned all three as Needed
        // even when the PR was merged. Lock that in.
        result.Needed.ShouldNotContain(RequirementKind.PlanAuthored);
        result.Needed.ShouldNotContain(RequirementKind.PlanReviewed);
        result.Needed.ShouldNotContain(RequirementKind.PlanPromoted);

        result.ObservationReasons!.ShouldContainKey(RequirementKind.PlanPromoted);
        result.ObservationReasons![RequirementKind.PlanPromoted].ShouldContain("merged");
    }

    // ─── Approved-but-unmerged: plan_reviewed Satisfied, promoted Fulfilling ─

    [Fact]
    public async Task NextReady_OpenApprovedPlanPr_ReviewedSatisfied_PromotedFulfilling()
    {
        await SeedApexAsync();
        var runner = NewRunnerWithRemote();
        StubLsRemote(runner, PlanBranch, exists: true);
        StubPrListSingle(runner, 204, PlanBranch);
        StubPrPoll(runner, 204, state: "OPEN", headRef: PlanBranch, reviewDecision: "APPROVED");

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // OPEN+APPROVED:
        //   plan_authored Fulfilling (PR exists, not merged)
        //   plan_reviewed Satisfied  (approved)
        //   plan_promoted Fulfilling (awaiting merge)
        result.Fulfilling.ShouldContain(RequirementKind.PlanAuthored);
        result.Satisfied.ShouldContain(RequirementKind.PlanReviewed);
        result.Fulfilling.ShouldContain(RequirementKind.PlanPromoted);
    }

    // ─── Failure handling: gh pr list errors → Needed with reason, no throw ─

    [Fact]
    public async Task NextReady_GhPrListFailure_DegradesToNeeded_NoException()
    {
        await SeedApexAsync();
        var runner = NewRunnerWithRemote();
        StubLsRemote(runner, PlanBranch, exists: true);
        // gh pr list fails — verb must NOT throw, must return all plan
        // kinds as Needed (per closed-loop spec §3.1).
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(1, "", "boom"));

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // gh pr list failure → plan composers all return Needed with the
        // captured reason. Reducer promotes plan_authored → Ready (Next),
        // plan_reviewed / plan_promoted remain Needed (prerequisites unmet).
        result.Next.ShouldContain(RequirementKind.PlanAuthored);
        result.Needed.ShouldContain(RequirementKind.PlanReviewed);
        result.Needed.ShouldContain(RequirementKind.PlanPromoted);

        result.ObservationReasons.ShouldNotBeNull();
        result.ObservationReasons![RequirementKind.PlanAuthored].ShouldNotBeNullOrWhiteSpace();
    }

    // ─── Failure handling: missing origin → all plan kinds Needed (slug gap) ─

    [Fact]
    public async Task NextReady_NoOriginRemote_PlanKindsNeeded_WithSlugReason()
    {
        await SeedApexAsync();
        // Simulate no origin remote; PlanObserver.TryResolveSlugAsync
        // returns "" and our scope captures the slug gap in
        // PlanPrFetchError so composers say "could not resolve repo slug"
        // rather than "no PR opened".
        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(128, "", "fatal: No such remote 'origin'"));
        StubLsRemote(runner, PlanBranch, exists: false);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // Slug-resolution failure surfaces via PlanPrFetchError → all plan
        // composers return Needed with the captured reason. plan_authored
        // is promoted to Ready (Next) by the reducer.
        result.Next.ShouldContain(RequirementKind.PlanAuthored);
        result.Needed.ShouldContain(RequirementKind.PlanReviewed);
        result.Needed.ShouldContain(RequirementKind.PlanPromoted);

        result.ObservationReasons.ShouldNotBeNull();
        result.ObservationReasons![RequirementKind.PlanAuthored].ShouldContain("slug");
    }

    // ─── Closed-unmerged plan PR → all three Needed (replan posture) ────

    [Fact]
    public async Task NextReady_ClosedUnmergedPlanPr_AllPlanKinds_Needed()
    {
        await SeedApexAsync();
        var runner = NewRunnerWithRemote();
        StubLsRemote(runner, PlanBranch, exists: true);
        StubPrListSingle(runner, 204, PlanBranch);
        StubPrPoll(runner, 204, state: "CLOSED", headRef: PlanBranch, reviewDecision: "REVIEW_REQUIRED");

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // Closed-unmerged plan PR → all three composers return Needed (replan
        // posture). plan_authored promoted to Ready by reducer.
        result.Next.ShouldContain(RequirementKind.PlanAuthored);
        result.Needed.ShouldContain(RequirementKind.PlanReviewed);
        result.Needed.ShouldContain(RequirementKind.PlanPromoted);
        result.ObservationReasons!.Values
            .Any(v => v.Contains("closed", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue();
    }

    // ─── Descendant item → walks parent chain to discover plan/{root}-{item} ─

    [Fact]
    public async Task NextReady_DescendantItem_ResolvesRootId_AndUsesHyphenPlanBranch()
    {
        // Apex 3043 with a descendant 3050; the verb must walk parents to
        // discover that 3043 is the root and inspect plan/3043-3050, not
        // plan/3050.
        var apex = new WorkItemBuilder().WithId(3043).WithType("Issue")
            .WithTitle("Apex").WithState("Doing").Build();
        var child = new WorkItemBuilder().WithId(3050).WithType("Issue")
            .WithTitle("Child").WithState("To Do").WithParentId(3043).Build();
        await SeedAsync(apex, child);

        const string descPlanBranch = "plan/3043-3050";
        var runner = NewRunnerWithRemote();
        StubLsRemote(runner, descPlanBranch, exists: true);
        StubPrListSingle(runner, 250, descPlanBranch);
        StubPrPoll(runner, 250, state: "MERGED", headRef: descPlanBranch, reviewDecision: "APPROVED");

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: 3050));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.PlanAuthored);
        result.Satisfied.ShouldContain(RequirementKind.PlanReviewed);
        result.Satisfied.ShouldContain(RequirementKind.PlanPromoted);
    }
}
