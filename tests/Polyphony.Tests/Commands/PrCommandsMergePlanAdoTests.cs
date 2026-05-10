using System.Net;
using System.Text.Json;
using Polyphony.Branching;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Locking;
using Polyphony.Manifest;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony pr merge-plan-ado</c> — the ADO
/// analogue of <c>polyphony pr merge-plan-pr</c>. Stubs git shell-outs
/// via <see cref="FakeProcessRunner"/> and substitutes <see cref="IAdoClient"/>
/// with a hand-rolled fake. Always exits 0 — error states surface in
/// <c>error_code</c> (routing-style envelope).
///
/// <para>Each test gets a fresh temp directory containing its own
/// <c>.polyphony/run.yaml</c> and <c>.polyphony/locks/</c> dir. The
/// FakeProcessRunner is wired so <c>git rev-parse --show-toplevel</c>
/// returns that temp dir, which makes <see cref="RunLockPathResolver"/>
/// place the lock file there too.</para>
/// </summary>
public sealed class PrCommandsMergePlanAdoTests : CommandTestBase, IDisposable
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private readonly string _tempDir;
    private readonly string _manifestPath;

    public PrCommandsMergePlanAdoTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyphony-merge-ado-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "run.yaml");
    }

    void IDisposable.Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        base.Dispose();
    }

    private (PrCommands Command, FakeProcessRunner Runner, FakeAdoClient Ado) CreateCommand(FakeAdoClient? ado = null)
    {
        ado ??= new FakeAdoClient();
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new RunLockStore(), new RunLockPathResolver(git), ado);
        return (cmd, runner, ado);
    }

    private void SeedManifest(int rootId, Dictionary<string, int>? planGenerations = null,
        List<MergedPlanPrEntry>? ledger = null)
    {
        var manifest = new RunManifest
        {
            Schema = 1,
            RootId = rootId,
            PlatformProject = "dev.azure.com/myorg/myproj",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = planGenerations ?? new Dictionary<string, int>(StringComparer.Ordinal),
            MergedPlanPrs = ledger ?? new List<MergedPlanPrEntry>(),
        };
        RunManifestStore.Save(_manifestPath, manifest);
    }

    private RunManifest LoadManifest() => RunManifestStore.LoadOrThrow(_manifestPath);

    private void StubEnvironmentDefaults(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["rev-parse", "--show-toplevel"], new ProcessResult(0, _tempDir + "\n", ""));
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, Path.Combine(_tempDir, ".git") + "\n", ""));
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, "https://dev.azure.com/myorg/myproj/_git/myrepo\n", ""));
    }

    private static void StubStatusClean(FakeProcessRunner runner)
        => runner.WhenExact("git", ["status", "--porcelain"], new ProcessResult(0, "", ""));

    private static void StubStatusDirty(FakeProcessRunner runner)
        => runner.WhenExact("git", ["status", "--porcelain"], new ProcessResult(0, " M file.txt\n", ""));

    private static void StubFetch(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["fetch", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubCheckout(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));

    private static void StubResetHard(FakeProcessRunner runner, string refspec)
        => runner.WhenExact("git", ["reset", "--hard", refspec], new ProcessResult(0, "", ""));

    private static void StubAdd(FakeProcessRunner runner, string pathspec)
        => runner.WhenExact("git", ["add", "--", pathspec], new ProcessResult(0, "", ""));

    private static void StubCommit(FakeProcessRunner runner)
        => runner.WhenStartsWith("git", ["commit", "-m"], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch, ProcessResult? result = null)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], result ?? new ProcessResult(0, "", ""));

    private void StubGitShowManifest(FakeProcessRunner runner, string branch, string yamlContent, bool missing = false)
    {
        var refspec = $"origin/{branch}:{_manifestPath}";
        var result = missing
            ? new ProcessResult(128, "", $"fatal: path '{_manifestPath}' does not exist in 'origin/{branch}'")
            : new ProcessResult(0, yamlContent, "");
        runner.WhenExact("git", ["show", refspec], result);
    }

    private static string MakeBodyWithSnapshot(IDictionary<string, int> snapshot, bool requestsParentChange = false)
    {
        var lines = new List<string>
        {
            "---",
            $"requests_parent_change: {(requestsParentChange ? "true" : "false")}",
            "ancestor_plan_generations:",
        };
        foreach (var (key, value) in snapshot)
            lines.Add($"  \"{key}\": {value}");
        lines.Add("---");
        lines.Add("");
        lines.Add("Plan PR body.");
        return string.Join("\n", lines);
    }

    private static AdoPullRequestPollData MakePoll(
        int number, string state, string headRef, string baseRef,
        string headOid = "abc123", string? mergeCommit = null, string body = "")
        => new()
        {
            Number = number,
            State = state,
            ReviewDecision = "APPROVED",
            Mergeable = "MERGEABLE",
            HeadRefName = headRef,
            HeadRefOid = headOid,
            BaseRefName = baseRef,
            MergedAt = state == "MERGED" ? DateTime.UtcNow : null,
            MergeCommit = mergeCommit,
            Body = body,
            Reviews = Array.Empty<AdoPullRequestReview>(),
        };

    private static PrMergePlanAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergePlanAdoResult)!;

    // ─── Input validation ───────────────────────────────────────────────

    [Theory]
    [InlineData("",   "p", "r", "--organization")]
    [InlineData("o",  "",  "r", "--project")]
    [InlineData("o",  "p", "",  "--repository")]
    public async Task EmptyIdentifier_RoutesInvalidArgument(string organization, string project, string repository, string missingFlag)
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(organization, project, repository, rootId: 100, itemId: 100, prNumber: 42));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr merge-plan-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
    }

    [Fact]
    public async Task RootIdInvalid_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 0, itemId: 100, prNumber: 42));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("root-id");
    }

    [Fact]
    public async Task ItemIdInvalid_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 0, prNumber: 42));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task PrNumberInvalid_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 5678, prNumber: 0));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task ParentIdEqualsItem_RoutesInvalidArgument()
    {
        var (cmd, _, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 5678, prNumber: 42, parentItemId: 5678));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    // ─── Lock contention ────────────────────────────────────────────────

    [Fact]
    public async Task LockHeld_RoutesLockHeld()
    {
        var (cmd, runner, _) = CreateCommand();
        StubEnvironmentDefaults(runner);

        // Rev 4.2: lock lives at <git-common-dir>/polyphony/<root_id>/locks/run.lock.
        var lockDir = Path.Combine(_tempDir, ".git", "polyphony", "100", "locks");
        Directory.CreateDirectory(lockDir);
        var lockFile = Path.Combine(lockDir, "run.lock");
        File.WriteAllText(lockFile,
            "schema: 1\nroot_id: 100\nlock_token: existing\nacquired_by: someone\nacquired_at: 2026-05-06T00:00:00Z\nttl_until: 2099-01-01T00:00:00Z\n");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("lock_held");
    }

    // ─── Branch derivation (pre-platform errors carry head/base) ────────

    [Fact]
    public async Task RootPlan_HeadIsRootPlanBranch()
    {
        var (cmd, runner, _) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusDirty(runner);  // short-circuit before merge

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.HeadBranch.ShouldBe("plan/100");
        result.BaseBranch.ShouldBe("feature/100");
        result.IsRootPlan.ShouldBeTrue();
        result.ItemKey.ShouldBe("root");
        result.ErrorCode.ShouldBe("worktree_dirty");
    }

    [Fact]
    public async Task DescendantPlan_DerivesPlanBranches()
    {
        var (cmd, runner, _) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusDirty(runner);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 5678, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.HeadBranch.ShouldBe("plan/100-5678");
        result.BaseBranch.ShouldBe("plan/100");
        result.ItemKey.ShouldBe("5678");
    }

    // ─── Pre-merge poll: identity validation ────────────────────────────

    [Fact]
    public async Task PrHeadRefMismatch_RoutesPrIdentityMismatch()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.PollData = MakePoll(42, "OPEN", headRef: "wrong/branch", baseRef: "feature/100");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_identity_mismatch");
        result.Error!.ShouldContain("'wrong/branch'");
    }

    [Fact]
    public async Task PrBaseRefMismatch_RoutesPrIdentityMismatch()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100", baseRef: "wrong/target");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_identity_mismatch");
    }

    [Fact]
    public async Task PrNotFound_RoutesPrNotFound()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.PollData = null;  // simulates 404 from list endpoint

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task PrUnexpectedState_RoutesPrStateInvalid()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.PollData = MakePoll(42, "CLOSED", headRef: "plan/100", baseRef: "feature/100");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_state_invalid");
    }

    // ─── Stale-generation refusal (P6) ──────────────────────────────────

    [Fact]
    public async Task DescendantPlan_StaleSnapshot_RoutesStaleGeneration()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100, planGenerations: new() { ["root"] = 5 });

        // Remote manifest at the feature branch tip says root=5 — that's
        // newer than the snapshot the PR body embedded (root=2).
        StubGitShowManifest(runner, "feature/100",
            "schema: 1\nroot_id: 100\nplatform_project: x\ncreated_at: 2026-01-01T00:00:00Z\ncreated_by: test\n" +
            "branch_model_version: 1\nplan_generations:\n  root: 5\n");

        var staleBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2 });
        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100-5678", baseRef: "plan/100", body: staleBody);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 5678, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("stale_generation");
        result.StaleAncestors.ShouldNotBeNull();
        result.StaleAncestors!.Count.ShouldBe(1);
        result.StaleAncestors[0].AncestorKey.ShouldBe("root");
        result.StaleAncestors[0].SnapshotGeneration.ShouldBe(2);
        result.StaleAncestors[0].CurrentGeneration.ShouldBe(5);
    }

    [Fact]
    public async Task DescendantPlan_NoSnapshotInBody_RoutesStaleGeneration()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100, planGenerations: new() { ["root"] = 1 });
        StubGitShowManifest(runner, "feature/100",
            "schema: 1\nroot_id: 100\nplatform_project: x\ncreated_at: 2026-01-01T00:00:00Z\ncreated_by: test\n" +
            "branch_model_version: 1\nplan_generations:\n  root: 1\n");

        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100-5678", baseRef: "plan/100",
            body: "no front matter here");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 5678, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("stale_generation");
        result.Error!.ShouldContain("ancestor_plan_generations");
    }

    [Fact]
    public async Task RootPlan_SkipsStalenessCheck()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        StubAdd(runner, _manifestPath);
        StubCommit(runner);
        StubPush(runner, "feature/100");
        SeedManifest(100, planGenerations: new() { ["root"] = 1 });

        // Root plan body has NO snapshot — but the verb skips the staleness
        // check for root plans entirely, so this should still merge.
        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100", baseRef: "feature/100",
            body: "root plan body — no front matter");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed",
            MergeCommitSha: "merge123",
            HttpStatus: 200,
            ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
    }

    // ─── Happy paths ────────────────────────────────────────────────────

    [Fact]
    public async Task OpenPr_CompletesAndRecordsLedger()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        StubAdd(runner, _manifestPath);
        StubCommit(runner);
        StubPush(runner, "feature/100");
        SeedManifest(100, planGenerations: new() { ["root"] = 1 });

        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100", baseRef: "feature/100");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed",
            MergeCommitSha: "merge-sha-xyz",
            HttpStatus: 200,
            ErrorBody: null);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeFalse();
        result.MergeCommit.ShouldBe("merge-sha-xyz");
        result.ManifestRecorded.ShouldBeTrue();
        result.ManifestPushed.ShouldBeTrue();
        result.PreviousGeneration.ShouldBe(1);
        result.CurrentGeneration.ShouldBe(2);
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");

        ado.CompleteCallCount.ShouldBe(1);
        ado.LastHeadShaSent.ShouldBe("abc123");

        // Manifest mutation landed
        var manifest = LoadManifest();
        manifest.MergedPlanPrs.Count.ShouldBe(1);
        manifest.MergedPlanPrs[0].PrNumber.ShouldBe(42);
        manifest.MergedPlanPrs[0].MergeCommit.ShouldBe("merge-sha-xyz");
    }

    [Fact]
    public async Task AlreadyMergedPr_ReusesMergeShaWithoutCallingComplete()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        StubAdd(runner, _manifestPath);
        StubCommit(runner);
        StubPush(runner, "feature/100");
        SeedManifest(100, planGenerations: new() { ["root"] = 1 });

        ado.PollData = MakePoll(42, "MERGED", headRef: "plan/100", baseRef: "feature/100",
            mergeCommit: "preexisting-sha");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("preexisting-sha");

        ado.CompleteCallCount.ShouldBe(0);  // recovery path: no complete call
    }

    [Fact]
    public async Task AlreadyMergedPr_MissingMergeSha_RoutesMissingMergeCommit()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);

        ado.PollData = MakePoll(42, "MERGED", headRef: "plan/100", baseRef: "feature/100",
            mergeCommit: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("missing_merge_commit");
    }

    [Fact]
    public async Task IdempotentSecondCall_NoLedgerMutation()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        SeedManifest(100,
            planGenerations: new() { ["root"] = 1 },
            ledger: new()
            {
                new MergedPlanPrEntry
                {
                    ItemKey = "root",
                    PrNumber = 42,
                    MergeCommit = "preexisting-sha",
                    PreviousGeneration = 0,
                    CurrentGeneration = 1,
                    RecordedAt = DateTime.UtcNow,
                }
            });

        ado.PollData = MakePoll(42, "MERGED", headRef: "plan/100", baseRef: "feature/100",
            mergeCommit: "preexisting-sha");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBeEmpty();
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.ManifestRecorded.ShouldBeFalse();
        result.ManifestPushed.ShouldBeFalse();
    }

    // ─── Complete-PR routable failures ──────────────────────────────────

    [Fact]
    public async Task CompletePr_StaleHead_RoutesStaleHead()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100", baseRef: "feature/100");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "stale_head", MergeCommitSha: null, HttpStatus: 409,
            ErrorBody: "head moved");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("stale_head");
    }

    [Fact]
    public async Task CompletePr_NotFound_RoutesPrNotFound()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100", baseRef: "feature/100");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "not_found", MergeCommitSha: null, HttpStatus: 404, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task CompletePr_NotMergeable_RoutesAdoCompleteFailed()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100", baseRef: "feature/100");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "not_mergeable", MergeCommitSha: null, HttpStatus: 400,
            ErrorBody: "policy refused");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("ado_complete_failed");
    }

    [Fact]
    public async Task CompletePr_MissingMergeSha_RoutesMissingMergeCommit()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100", baseRef: "feature/100");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: null, HttpStatus: 200, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("missing_merge_commit");
    }

    // ─── Wire-level failures ────────────────────────────────────────────

    [Fact]
    public async Task NoPat_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.ThrowOnPoll = new InvalidOperationException("PAT required (set AZURE_DEVOPS_EXT_PAT)");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task PollHttp401_RoutesNoPat()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.ThrowOnPoll = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("no_pat");
    }

    [Fact]
    public async Task PollTimeout_RoutesAdoTimeout()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        SeedManifest(100);
        ado.ThrowOnPoll = new TimeoutException("attempts exhausted");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("ado_timeout");
    }

    // ─── Manifest push rejection (rollback) ─────────────────────────────

    [Fact]
    public async Task ManifestPushRejected_RoutesManifestPushRejected()
    {
        var (cmd, runner, ado) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        StubAdd(runner, _manifestPath);
        StubCommit(runner);
        // Push rejected with non-fast-forward — the verb's catch maps this
        // to manifest_push_rejected and resets the worktree.
        StubPush(runner, "feature/100",
            new ProcessResult(1, "", "rejected: non-fast-forward"));
        SeedManifest(100, planGenerations: new() { ["root"] = 1 });

        ado.PollData = MakePoll(42, "OPEN", headRef: "plan/100", baseRef: "feature/100");
        ado.CompleteResult = new AdoCompletePullRequestResult(
            Status: "completed", MergeCommitSha: "merge-sha", HttpStatus: 200, ErrorBody: null);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanAdo(Org, Project, Repo, rootId: 100, itemId: 100, prNumber: 42,
                manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("manifest_push_rejected");
        result.Merged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("merge-sha");
        result.ManifestRecorded.ShouldBeFalse();
        result.ManifestPushed.ShouldBeFalse();
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public AdoPullRequestPollData? PollData { get; set; }
        public Exception? ThrowOnPoll { get; set; }
        public AdoCompletePullRequestResult? CompleteResult { get; set; }
        public Exception? ThrowOnComplete { get; set; }
        public int CompleteCallCount { get; private set; }
        public string? LastHeadShaSent { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> GetPullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> CreatePullRequestAsync(
            string organization, string project, string repository,
            string sourceBranch, string targetBranch, string title,
            string description, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
            string organization, string project, string repositoryId,
            int pullRequestId, CancellationToken ct = default)
        {
            if (ThrowOnPoll is not null) throw ThrowOnPoll;
            return Task.FromResult(PollData);
        }

        public Task<bool> SetPullRequestVoteAsync(
            string organization, string project, string repository,
            int pullRequestId, string reviewerId, int vote,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string lastMergeSourceCommitSha,
            CancellationToken ct = default)
        {
            CompleteCallCount++;
            LastHeadShaSent = lastMergeSourceCommitSha;
            if (ThrowOnComplete is not null) throw ThrowOnComplete;
            if (CompleteResult is null)
                throw new InvalidOperationException("Test fake: CompleteResult not configured.");
            return Task.FromResult(CompleteResult);
        }

        public Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBody,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    
        public Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();
}
}
