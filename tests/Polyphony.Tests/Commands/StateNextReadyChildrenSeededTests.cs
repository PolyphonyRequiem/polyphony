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
/// closed-loop PR #3: <see cref="PlanObserver.IsParentSeededAsync"/> drives
/// the <c>children_seeded</c> disposition from the
/// <c>polyphony:planned</c> tag presence on the work item, NOT from
/// child-state heuristics.
/// </summary>
/// <remarks>
/// <para>
/// Pre-PR-#3 the verb derived <c>children_seeded</c> from "any non-Done
/// child" semantics — which mis-labelled an apex with seeded children
/// (any of which were still in flight) as <c>Fulfilling</c>, and an apex
/// where the seeder had legitimately produced zero children (the
/// indivisible case from §3.4) as <c>Needed</c> for ever. The fix wires
/// the canonical write-once <c>polyphony:planned</c> tag (set by
/// <c>plan seed-children</c>) directly into the observation scope.
/// </para>
/// <para>
/// Mocks the <c>twig show ... --output json</c> shell-out via
/// <see cref="FakeProcessRunner"/> — same pattern as
/// <see cref="Tests.Sdlc.Observers.PlanObserverTests"/> shipped in PR #1.
/// Plan-kind shell-outs are stubbed to "no signal" so they do not
/// confound the children_seeded assertions.
/// </para>
/// </remarks>
public sealed class StateNextReadyChildrenSeededTests : CommandTestBase
{
    private const int ApexId = 3043;
    private const string PlanBranch = "plan/3043";
    private const string OriginUrl = "https://github.com/acme/repo.git";

    private StateCommands CreateCommand(FakeProcessRunner runner, ProcessConfig? configOverride = null)
    {
        var config = configOverride ?? Config;
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var planObserver = new PlanObserver(git, gh, twig);
        return new StateCommands(twig, git, gh, runner, Repository, config, planObserver);
    }

    /// <summary>
    /// Mark the default Issue type explicitly decomposable so the
    /// requirement-set deriver always includes <c>children_seeded</c> —
    /// the tests below assert disposition for that kind, which is only
    /// derived when <c>decomposable &amp;&amp; plannable</c> is true (see
    /// <c>RequirementSetDeriver.cs:154</c>). Without this the
    /// <see cref="RequirementInputResolver"/> infers
    /// <c>decomposable=false</c> for any Issue with zero children and
    /// <c>children_seeded</c> drops out of the set, masking the very
    /// disposition we are validating.
    /// </summary>
    private ProcessConfig DecomposableIssueConfig()
    {
        Config.Types["Issue"].Decomposable = true;
        return Config;
    }

    /// <summary>Configure the runner with a minimal "no plan signal"
    /// baseline (origin remote resolves, ls-remote shows no branch, gh
    /// pr list returns []) so plan-kind composers degrade cleanly to
    /// Needed without bleeding into the children_seeded assertions.</summary>
    private static FakeProcessRunner NewRunnerWithPlanBaseline()
    {
        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, OriginUrl + "\n", ""));
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{PlanBranch}"],
            new ProcessResult(0, "", ""));
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
        return runner;
    }

    private static void StubTwigShowWithTags(FakeProcessRunner runner, int itemId, string tags)
        => runner.WhenExact("twig", ["show", itemId.ToString(), "--output", "json"],
            new ProcessResult(0, $$"""{"id":{{itemId}},"title":"Item","tags":"{{tags}}"}""", ""));

    private static void StubTwigShowError(FakeProcessRunner runner, int itemId)
        => runner.WhenExact("twig", ["show", itemId.ToString(), "--output", "json"],
            new ProcessResult(1, "", "twig: cache unreachable"));

    /// <summary>Register a twig-show responder that throws an exception
    /// instead of returning a non-zero process result. Exercises the
    /// outer try/catch in <c>FetchPlannedTagAsync</c>.</summary>
    private static void StubTwigShowThrows(FakeProcessRunner runner, int itemId)
        => runner.WhenAsync(
            (e, a) => e == "twig" && a.Count >= 2 && a[0] == "show" && a[1] == itemId.ToString(),
            (_, _) => throw new InvalidOperationException("simulated twig client failure"));

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

    // ─── Tag absent → Needed regardless of child count ─────────────────

    [Fact]
    public async Task NextReady_NoPlannedTag_ChildrenSeededNeeded_RegardlessOfChildCount()
    {
        // Apex 3043 reproduction: seeder never ran → no tag → children_seeded
        // must report Needed even though there are zero children. Pre-PR-#3
        // the verb hit the "(0, _, _) => Needed" arm of the legacy switch
        // and got the same answer for the wrong reason.
        await SeedApexAsync();
        var runner = NewRunnerWithPlanBaseline();
        StubTwigShowWithTags(runner, ApexId, tags: "");

        var cmd = CreateCommand(runner, DecomposableIssueConfig());
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ChildrenSeeded);
        result.Needed.ShouldContain(RequirementKind.ChildrenSeeded);
        result.ObservationReasons.ShouldNotBeNull();
        result.ObservationReasons!.ShouldContainKey(RequirementKind.ChildrenSeeded);
        result.ObservationReasons![RequirementKind.ChildrenSeeded]
            .ShouldContain("polyphony:planned", Case.Sensitive);
        result.ObservationReasons![RequirementKind.ChildrenSeeded]
            .ShouldContain("not present");
    }

    [Fact]
    public async Task NextReady_NoPlannedTag_WithSeededChildren_ChildrenSeededStillNeeded()
    {
        // Defensive: even if children exist in the cache, without the
        // canonical tag the verb must NOT call children_seeded Satisfied.
        // The tag is the only authoritative signal that the seeder ran.
        await SeedApexWithChildrenAsync(childCount: 3);
        var runner = NewRunnerWithPlanBaseline();
        StubTwigShowWithTags(runner, ApexId, tags: "polyphony;some-other-tag");

        var cmd = CreateCommand(runner, DecomposableIssueConfig());
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ChildrenSeeded);
        result.Needed.ShouldContain(RequirementKind.ChildrenSeeded);
        result.ObservationReasons!.ShouldContainKey(RequirementKind.ChildrenSeeded);
        result.ObservationReasons![RequirementKind.ChildrenSeeded].ShouldContain("not present");
    }

    // ─── Tag present + children → Satisfied ─────────────────────────────

    [Fact]
    public async Task NextReady_PlannedTagPresent_WithChildren_ChildrenSeededSatisfied()
    {
        await SeedApexWithChildrenAsync(childCount: 3);
        var runner = NewRunnerWithPlanBaseline();
        StubTwigShowWithTags(runner, ApexId, tags: "polyphony;polyphony:planned");

        var cmd = CreateCommand(runner, DecomposableIssueConfig());
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.ChildrenSeeded);
        result.Needed.ShouldNotContain(RequirementKind.ChildrenSeeded);
        result.Fulfilling.ShouldNotContain(RequirementKind.ChildrenSeeded);

        result.ObservationReasons!.ShouldContainKey(RequirementKind.ChildrenSeeded);
        result.ObservationReasons![RequirementKind.ChildrenSeeded].ShouldContain("present");
        result.ObservationReasons![RequirementKind.ChildrenSeeded].ShouldContain("polyphony:planned");
    }

    // ─── Tag present + zero children (indivisible case from PR #7) ─────

    [Fact]
    public async Task NextReady_PlannedTagPresent_ZeroChildren_ChildrenSeededSatisfied_IndivisibleCase()
    {
        // The "decomposable but indivisible" apex: planner ran, decided
        // no children were warranted, stamped polyphony:planned. Pre-PR-#3
        // this returned Needed forever because the legacy switch's
        // (0, _, _) => Needed arm ignored the tag.
        await SeedApexAsync();
        var runner = NewRunnerWithPlanBaseline();
        StubTwigShowWithTags(runner, ApexId, tags: "polyphony:planned");

        var cmd = CreateCommand(runner, DecomposableIssueConfig());
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.ChildrenSeeded);
        result.Needed.ShouldNotContain(RequirementKind.ChildrenSeeded);

        result.ObservationReasons!.ShouldContainKey(RequirementKind.ChildrenSeeded);
        result.ObservationReasons![RequirementKind.ChildrenSeeded].ShouldContain("present");
    }

    // ─── twig read error degrades to Needed; never throws ──────────────

    [Fact]
    public async Task NextReady_TwigShowReturnsError_ChildrenSeededNeeded_NoException()
    {
        // twig show exits non-zero → IsParentSeededAsync swallows and
        // returns false → children_seeded reports Needed with the
        // "not present" reason. The verb must still exit 0.
        await SeedApexAsync();
        var runner = NewRunnerWithPlanBaseline();
        StubTwigShowError(runner, ApexId);

        var cmd = CreateCommand(runner, DecomposableIssueConfig());
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ChildrenSeeded);
        result.ObservationReasons!.ShouldContainKey(RequirementKind.ChildrenSeeded);
        result.ObservationReasons![RequirementKind.ChildrenSeeded].ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task NextReady_TwigShowThrows_ChildrenSeededNeeded_NoExceptionEscapes()
    {
        // Defensive layer: even if the twig client throws (e.g. future
        // tightening of the observer, cancellation, or a non-swallowed
        // path), FetchPlannedTagAsync must catch and surface the error
        // via PlannedTagFetchError → Needed with a non-empty reason.
        await SeedApexAsync();
        var runner = NewRunnerWithPlanBaseline();
        StubTwigShowThrows(runner, ApexId);

        var cmd = CreateCommand(runner, DecomposableIssueConfig());
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ChildrenSeeded);
        result.ObservationReasons!.ShouldContainKey(RequirementKind.ChildrenSeeded);
        result.ObservationReasons![RequirementKind.ChildrenSeeded].ShouldNotBeNullOrWhiteSpace();
    }
}
