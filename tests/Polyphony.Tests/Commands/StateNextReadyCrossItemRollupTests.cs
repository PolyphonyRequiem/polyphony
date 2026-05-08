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
/// Integration tests for closed-loop PR #5: cross-item rollup of
/// <c>item_satisfied</c> and <c>implementation_merged</c> from direct
/// children into the parent's <c>state next-ready</c> envelope.
/// </summary>
/// <remarks>
/// <para>
/// PR #4 wired per-item <c>implementation_merged</c> to live impl-PR
/// state. PR #5 adds the cross-item piece: an MG-style item whose own
/// impl PR is merged is NOT Satisfied when any implementable child is
/// still in flight (closed-loop §3.1 row 5). The rollup is recursive,
/// depth-bounded (default 5; overridable via
/// <c>POLYPHONY_NEXTREADY_ROLLUP_DEPTH</c>), and degrades gracefully on
/// every failure mode (cycle, depth-cap, per-child fault).
/// </para>
/// <para>
/// Uses the Task type (implementable-only) for both parent and children
/// so the requirement set is just <c>impl_merged + item_satisfied</c> —
/// the rollup is the dominant signal and the assertions stay focused on
/// what PR #5 actually changes. The
/// <see cref="StateNextReadyImplementationTests"/> file covers the
/// Issue-style parent path with the existing 3-child fixture.
/// </para>
/// </remarks>
public sealed class StateNextReadyCrossItemRollupTests : CommandTestBase
{
    private const int ParentId = 5000;
    private const string OriginUrl = "https://github.com/acme/repo.git";
    private const string RollupDepthEnvVar = "POLYPHONY_NEXTREADY_ROLLUP_DEPTH";

    /// <summary>Scoped override for the rollup-depth env var. Restores
    /// the prior value on dispose so a parallel sibling test class never
    /// inherits a stale value — the shared <c>ConsoleTestLock</c> on the
    /// test base serialises async tests against each other but does NOT
    /// scope process-wide env-var mutations.</summary>
    private sealed class ScopedEnv : IDisposable
    {
        private readonly string _name;
        private readonly string? _prior;
        public ScopedEnv(string name, string? value)
        {
            _name = name;
            _prior = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _prior);
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

    /// <summary>Register catch-all responders for every shell-out the
    /// observers issue. Specific impl-PR matchers MUST be registered
    /// BEFORE this so they win the first-match dispatch in
    /// <see cref="FakeProcessRunner"/>.</summary>
    private static void BindCatchAlls(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, OriginUrl + "\n", ""));
        runner.WhenStartsWith("git", ["ls-remote"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("twig", ["show"], new ProcessResult(0,
            """{"id":0,"title":"x","tags":""}""", ""));
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
    }

    /// <summary>Stub a successful merged impl PR for one specific
    /// <c>impl/{root}-{item}</c> branch. <c>--head &lt;branch&gt;</c>
    /// match disambiguates from the catch-all PR-list and from sibling
    /// impl branches.</summary>
    private static void StubImplPrMerged(FakeProcessRunner runner, string implBranch, int prNumber)
    {
        runner.When(
            (e, a) => e == "gh"
                && a.Count >= 4 && a[0] == "pr" && a[1] == "list"
                && HasHeadFilter(a, implBranch),
            new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"headRefName":"{{implBranch}}","url":"https://gh/pr/{{prNumber}}"}]""",
                ""));
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(0, BuildPrPollJson(prNumber, "MERGED", implBranch), ""));
    }

    /// <summary>Stub an open impl PR for one specific impl branch — the
    /// rollup composer should report Fulfilling for that branch.</summary>
    private static void StubImplPrOpen(FakeProcessRunner runner, string implBranch, int prNumber)
    {
        runner.When(
            (e, a) => e == "gh"
                && a.Count >= 4 && a[0] == "pr" && a[1] == "list"
                && HasHeadFilter(a, implBranch),
            new ProcessResult(0,
                $$"""[{"number":{{prNumber}},"headRefName":"{{implBranch}}","url":"https://gh/pr/{{prNumber}}"}]""",
                ""));
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(0, BuildPrPollJson(prNumber, "OPEN", implBranch), ""));
    }

    private static string BuildPrPollJson(int prNumber, string state, string implBranch) =>
        $$"""
        {
          "number": {{prNumber}},
          "state": "{{state}}",
          "reviewDecision": "",
          "mergeable": "MERGEABLE",
          "headRefName": "{{implBranch}}",
          "headRefOid": "abc123",
          "baseRefName": "main",
          "mergedAt": null,
          "mergeCommit": null,
          "body": "",
          "reviews": []
        }
        """;

    private static bool HasHeadFilter(IReadOnlyList<string> args, string branch)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--head" && args[i + 1] == branch) return true;
        }
        return false;
    }

    /// <summary>Seed a parent Task plus <paramref name="childCount"/>
    /// child Tasks. Returns the assigned child IDs in registration order
    /// so tests can refer to them (e.g. for impl-PR stubbing).</summary>
    private async Task<int[]> SeedParentWithTaskChildrenAsync(int childCount)
    {
        var parent = new WorkItemBuilder()
            .WithId(ParentId).WithType("Task").WithTitle("Parent").WithState("Doing").Build();
        var items = new List<Twig.Domain.Aggregates.WorkItem> { parent };
        var ids = new int[childCount];
        for (var i = 0; i < childCount; i++)
        {
            ids[i] = ParentId + 100 + i;
            items.Add(new WorkItemBuilder()
                .WithId(ids[i]).WithType("Task").WithTitle($"Child {i}").WithState("To Do")
                .WithParentId(ParentId).Build());
        }
        await SeedAsync(items.ToArray());
        return ids;
    }

    /// <summary>Plan/impl branch helpers — the parent's own plan branch
    /// is <c>plan/{parent}</c>; descendant branches use the
    /// <c>{root}-{item}</c> form. Tests use these exclusively to keep
    /// the branch grammar in one place.</summary>
    private static string ParentImplBranch() => $"impl/{ParentId}-{ParentId}";
    private static string ChildImplBranch(int childId) => $"impl/{ParentId}-{childId}";

    // ─── (1) Parent + 1 child, both merged → parent Satisfied ──────────

    [Fact]
    public async Task NextReady_ParentAndOneChild_BothMerged_ItemSatisfied()
    {
        // Closed-loop §3.1 row 5/6 happy path: parent's own impl PR and
        // the single child's impl PR are both merged → both reduce to
        // item_satisfied=Satisfied → rollup is a no-op (worst stays
        // Satisfied) → parent's item_satisfied stays Satisfied. The
        // ObservationReasons dict carries no demotion entry for
        // item_satisfied because no demotion fired.
        var ids = await SeedParentWithTaskChildrenAsync(childCount: 1);
        var runner = new FakeProcessRunner();
        StubImplPrMerged(runner, ParentImplBranch(), prNumber: 100);
        StubImplPrMerged(runner, ChildImplBranch(ids[0]), prNumber: 101);
        BindCatchAlls(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ParentId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.ImplementationMerged);
        result.Satisfied.ShouldContain(RequirementKind.ItemSatisfied);
        result.Needed.ShouldNotContain(RequirementKind.ItemSatisfied);
    }

    // ─── (2) Parent + 1 child Needed → parent Needed, reason cites child

    [Fact]
    public async Task NextReady_ParentMerged_OneChildNoPr_DemotedWithChildIdInReason()
    {
        // Parent's own impl PR is merged; the single child has no impl
        // PR (catch-all gh pr list returns []) → child's reduced
        // impl_merged is reducer-promoted to Ready (no prerequisites
        // within the child's set). The worst-of in
        // ComposeImplementationMerged drops parent's impl_merged from
        // Satisfied to the worst-child disposition (Ready) and surfaces
        // the child id in the reason — that is the PR #5 cross-item
        // rollup observable.
        var ids = await SeedParentWithTaskChildrenAsync(childCount: 1);
        var runner = new FakeProcessRunner();
        StubImplPrMerged(runner, ParentImplBranch(), prNumber: 100);
        BindCatchAlls(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ParentId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        // Worst-of demotion pulls parent OFF Satisfied — that is the
        // testable observable. The exact downgraded disposition (Ready
        // here, because the child's reduced impl_merged was
        // reducer-promoted from Needed to Ready) is implementation
        // detail that the reason string makes precise.
        result.Satisfied.ShouldNotContain(RequirementKind.ImplementationMerged);
        // item_satisfied terminal can't promote to Satisfied while
        // impl_merged is below Satisfied — confirms the demotion
        // cascades through the within-item edge.
        result.Satisfied.ShouldNotContain(RequirementKind.ItemSatisfied);

        result.ObservationReasons.ShouldNotBeNull();
        var implReason = result.ObservationReasons![RequirementKind.ImplementationMerged];
        implReason.ShouldContain("merged");
        implReason.ShouldContain($"child #{ids[0]}");
    }

    // ─── (3) Parent + 3 mixed children → worst (Needed) wins ───────────

    [Fact]
    public async Task NextReady_ThreeChildrenMixed_WorstNeededWinsAndCitesNeededChild()
    {
        // Parent merged; child A merged → reduced impl_merged Satisfied;
        // child B open → Fulfilling; child C no PR → reducer-promoted to
        // Ready (no within-item prerequisites). Worst-of must pick C
        // (Ready=1 beats Fulfilling=2 beats Satisfied=3 in
        // Disposition.Order ranking) and surface C's id in the reason —
        // callers want one structured pointer to the single offending
        // child rather than a join across all of them.
        var ids = await SeedParentWithTaskChildrenAsync(childCount: 3);
        var (mergedChild, openChild, neededChild) = (ids[0], ids[1], ids[2]);
        var runner = new FakeProcessRunner();
        StubImplPrMerged(runner, ParentImplBranch(), prNumber: 100);
        StubImplPrMerged(runner, ChildImplBranch(mergedChild), prNumber: 200);
        StubImplPrOpen(runner, ChildImplBranch(openChild), prNumber: 201);
        // neededChild deliberately falls through to the catch-all "no PR".
        BindCatchAlls(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ParentId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ImplementationMerged);

        var implReason = result.ObservationReasons![RequirementKind.ImplementationMerged];
        implReason.ShouldContain($"child #{neededChild}");
        implReason.ShouldNotContain($"child #{mergedChild}");
        implReason.ShouldNotContain($"child #{openChild}");
    }

    // ─── (4) Three-deep chain, all merged → parent Satisfied ───────────

    [Fact]
    public async Task NextReady_ThreeLevelChain_AllMerged_RollsUpThroughGrandchild()
    {
        // Parent → Child → Grandchild, every node has its own merged
        // impl PR. Validates that the recursive rollup reaches the
        // grandchild and the inner ApplyChildItemSatisfiedRollup call
        // (line 1002 of StateCommands.NextReady.cs) correctly carries
        // grandchild satisfaction up through the child snapshot.
        var parent = new WorkItemBuilder()
            .WithId(ParentId).WithType("Task").WithTitle("Parent").WithState("Doing").Build();
        var child = new WorkItemBuilder()
            .WithId(ParentId + 100).WithType("Task").WithTitle("Child").WithState("To Do")
            .WithParentId(ParentId).Build();
        var grandchild = new WorkItemBuilder()
            .WithId(ParentId + 200).WithType("Task").WithTitle("Grandchild").WithState("To Do")
            .WithParentId(ParentId + 100).Build();
        await SeedAsync(parent, child, grandchild);

        // Grandchild's root walks up to ParentId via ResolveRootIdAsync,
        // so its impl branch is impl/{ParentId}-{grandchildId}.
        var runner = new FakeProcessRunner();
        StubImplPrMerged(runner, ParentImplBranch(), prNumber: 100);
        StubImplPrMerged(runner, ChildImplBranch(ParentId + 100), prNumber: 200);
        StubImplPrMerged(runner, ChildImplBranch(ParentId + 200), prNumber: 300);
        BindCatchAlls(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ParentId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.ImplementationMerged);
        result.Satisfied.ShouldContain(RequirementKind.ItemSatisfied);
    }

    // ─── (5) Default depth=5 → deep chain truncates, parent demoted ────

    [Fact]
    public async Task NextReady_DeepChain_DefaultDepth_TruncationDemotesParentOffSatisfied()
    {
        // Set every node's impl PR to merged — without truncation the
        // worst-of would carry Satisfied all the way up. With the
        // default cap=5 the recursion exhausts its budget before
        // reaching the deepest descendant; that frame's
        // ChildRollupTruncated forces the local impl_merged composer to
        // Needed, which the within-item reducer then promotes back to
        // Ready (no prerequisites). Worst-of cascades the Ready up
        // through the parent chain → top's impl_merged ends up off
        // Satisfied. Truncation observability at the top is therefore
        // by demotion-vs-baseline; the truncated reason is captured
        // in the deepest scope's local reasons (lost across the
        // snapshot boundary) per the implementation's design.
        using var _ = new ScopedEnv(RollupDepthEnvVar, value: null);
        const int chainDepth = 7;
        await SeedChainAsync(depth: chainDepth);
        var runner = new FakeProcessRunner();
        StubAllChainImplPrsMerged(runner, chainDepth);
        BindCatchAlls(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ParentId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ImplementationMerged);
        result.Satisfied.ShouldNotContain(RequirementKind.ItemSatisfied);
    }

    // ─── (6) Env-var override → shallower truncation cap ───────────────

    [Fact]
    public async Task NextReady_EnvVarShrinksDepth_TruncationFiresEarlier()
    {
        // POLYPHONY_NEXTREADY_ROLLUP_DEPTH=2 means the recursion can
        // see the parent + one level of children but not deeper. A
        // 4-node chain therefore truncates at the second descendant
        // even though all impl PRs are merged → top demoted off
        // Satisfied. Pair test below proves the env var actually
        // narrows the cap (vs. always firing).
        using var _ = new ScopedEnv(RollupDepthEnvVar, value: "2");
        const int chainDepth = 4;
        await SeedChainAsync(depth: chainDepth);
        var runner = new FakeProcessRunner();
        StubAllChainImplPrsMerged(runner, chainDepth);
        BindCatchAlls(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ParentId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldNotContain(RequirementKind.ImplementationMerged);
    }

    [Fact]
    public async Task NextReady_EnvVarRaisesDepth_NoTruncation_FullyMergedChainSatisfies()
    {
        // Counter-test to PR #5's depth-cap matrix: with the budget
        // raised above the chain depth, the same all-merged 4-node
        // chain rolls up cleanly to Satisfied. Confirms the env-var
        // hook actually narrows the cap rather than always firing.
        using var _ = new ScopedEnv(RollupDepthEnvVar, value: "10");
        const int chainDepth = 4;
        await SeedChainAsync(depth: chainDepth);
        var runner = new FakeProcessRunner();
        StubAllChainImplPrsMerged(runner, chainDepth);
        BindCatchAlls(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ParentId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Satisfied.ShouldContain(RequirementKind.ImplementationMerged);
        result.Satisfied.ShouldContain(RequirementKind.ItemSatisfied);
    }

    // ─── (7) Cycle in cache → no infinite recursion, exit 0 ────────────

    [Fact]
    public async Task NextReady_CycleInCache_DegradesGracefullyExitZero()
    {
        // Synthetic cycle: parent.parent = child, child.parent = parent.
        // Real ADO trees can't cycle (parent_id is single-valued) but a
        // corrupted cache absolutely could; the verb's "always exit 0"
        // contract MUST hold. ComputeChildSnapshotAsync detects the
        // ancestor membership at the deepest recursion frame and
        // returns a snapshot with Error set rather than recursing
        // forever. The composers then surface the cycle via
        // ObservationReasons (a "cycle" / "rollup error" string) so
        // operators have something to drill into.
        var parent = new WorkItemBuilder()
            .WithId(ParentId).WithType("Task").WithTitle("Parent").WithState("Doing")
            .WithParentId(ParentId + 100).Build();
        var child = new WorkItemBuilder()
            .WithId(ParentId + 100).WithType("Task").WithTitle("Child").WithState("To Do")
            .WithParentId(ParentId).Build();
        await SeedAsync(parent, child);

        var runner = new FakeProcessRunner();
        BindCatchAlls(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ParentId));
        exit.ShouldBe(ExitCodes.Success);

        // Output is valid envelope JSON — the strict assertion is "no
        // throw and a deserialisable result"; the cycle reason itself
        // surfaces via the cycle-aware paths in ComputeChildSnapshotAsync
        // / ApplyChildItemSatisfiedRollup.
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.WorkItemId.ShouldBe(ParentId);
        result.ObservationReasons.ShouldNotBeNull();
    }

    /// <summary>Seed a linear ancestry chain rooted at
    /// <see cref="ParentId"/>. <paramref name="depth"/> = total node
    /// count along the chain (parent = 1, parent + child = 2, …). Each
    /// node has exactly one child except the leaf.</summary>
    private async Task SeedChainAsync(int depth)
    {
        var items = new List<Twig.Domain.Aggregates.WorkItem>(depth);
        for (var i = 0; i < depth; i++)
        {
            var id = ParentId + (i * 100);
            var builder = new WorkItemBuilder()
                .WithId(id).WithType("Task").WithTitle($"Node {i}")
                .WithState(i == 0 ? "Doing" : "To Do");
            if (i > 0) builder.WithParentId(ParentId + ((i - 1) * 100));
            items.Add(builder.Build());
        }
        await SeedAsync(items.ToArray());
    }

    /// <summary>Stub a merged impl PR for every node in a chain seeded
    /// by <see cref="SeedChainAsync"/>. Chain root is at
    /// <see cref="ParentId"/>; each node's impl branch is
    /// <c>impl/{ParentId}-{nodeId}</c> (root walks up to the same
    /// ParentId via <c>ResolveRootIdAsync</c>). PR numbers start at 9000
    /// to stay clear of the per-test fixed numbers used in the matrix
    /// tests above.</summary>
    private static void StubAllChainImplPrsMerged(FakeProcessRunner runner, int depth)
    {
        for (var i = 0; i < depth; i++)
        {
            var id = ParentId + (i * 100);
            StubImplPrMerged(runner, $"impl/{ParentId}-{id}", prNumber: 9000 + i);
        }
    }
}
