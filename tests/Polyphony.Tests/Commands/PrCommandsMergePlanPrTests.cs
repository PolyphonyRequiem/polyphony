using System.Text.Json;
using Polyphony.Branching;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Locking;
using Polyphony.Manifest;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony pr merge-plan-pr</c>. Stubs all
/// shell-outs (git status, git fetch, git remote get-url, git checkout,
/// git reset, git add, git commit, git push, git rev-parse, gh pr view,
/// gh pr merge) via <see cref="FakeProcessRunner"/> and asserts on the
/// JSON the verb emits plus the side-effects on the local manifest file
/// and the run-lock file.
///
/// <para>Each test gets a fresh temp directory containing its own
/// <c>.polyphony/run.yaml</c> and <c>.polyphony/locks/</c> dir. The
/// FakeProcessRunner is wired so <c>git rev-parse --show-toplevel</c>
/// returns that temp dir, which makes <see cref="RunLockPathResolver"/>
/// place the lock file there too.</para>
/// </summary>
public sealed class PrCommandsMergePlanPrTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly string _manifestPath;

    public PrCommandsMergePlanPrTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyphony-merge-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "run.yaml");
    }

    void IDisposable.Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        base.Dispose();
    }

    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new RunLockStore(), new RunLockPathResolver(git)), runner);
    }

    private void SeedManifest(int rootId, Dictionary<string, int>? planGenerations = null, List<MergedPlanPrEntry>? ledger = null)
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
            MergedPlanPrs = ledger ?? new List<MergedPlanPrEntry>(),
        };
        RunManifestStore.Save(_manifestPath, manifest);
    }

    private RunManifest LoadManifest() => RunManifestStore.LoadOrThrow(_manifestPath);

    /// <summary>Stubs the deterministic, environment-only commands every call needs (git toplevel + remote URL). Status is per-test.</summary>
    private void StubEnvironmentDefaults(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["rev-parse", "--show-toplevel"], new ProcessResult(0, _tempDir + "\n", ""));
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, "https://github.com/owner/repo.git\n", ""));
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

    /// <summary>Stub for <see cref="IGhClient.GetPullRequestPollDataAsync"/>.</summary>
    private static void StubPrPoll(
        FakeProcessRunner runner,
        int prNumber,
        string state,
        string headRefName,
        string baseRefName,
        string headRefOid = "abc123",
        string? mergeCommitSha = null,
        string mergedAt = "")
    {
        var mergeCommitClause = mergeCommitSha is null ? "null" : $$"""{"oid":"{{mergeCommitSha}}"}""";
        var mergedAtClause = string.IsNullOrEmpty(mergedAt) ? "null" : $"\"{mergedAt}\"";
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "APPROVED",
              "mergeable": "MERGEABLE",
              "headRefName": "{{headRefName}}",
              "headRefOid": "{{headRefOid}}",
              "baseRefName": "{{baseRefName}}",
              "mergedAt": {{mergedAtClause}},
              "mergeCommit": {{mergeCommitClause}},
              "body": "",
              "reviews": []
            }
            """;
        // poll-status uses a wide --json field set
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    /// <summary>Stub for <see cref="IGhClient.MergePullRequestAsync"/>'s primary `gh pr merge` call AND its follow-up `gh pr view` reconcile.</summary>
    private static void StubPrMergeSuccess(FakeProcessRunner runner, int prNumber, string mergeSha)
    {
        runner.WhenStartsWith("gh", ["pr", "merge", prNumber.ToString()], new ProcessResult(0, "", ""));
        // After a successful gh pr merge, GhClient runs a follow-up gh pr view to populate the merge SHA.
        // The poll-status stub already responds to "pr view" with the OPEN state — we override here
        // so the follow-up reconcile reports MERGED with the SHA.
    }

    private static PrMergePlanPrResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrMergePlanPrResult)!;

    // ─── Input validation ───────────────────────────────────────────────

    [Fact]
    public async Task RootIdInvalid_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 0, itemId: 100, prNumber: 42));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("config_error");
        result.Error.ShouldNotBeNull(); result.Error!.ShouldContain("root-id");
    }

    [Fact]
    public async Task ItemIdInvalid_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 0, prNumber: 42));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("config_error");
        result.Error.ShouldNotBeNull(); result.Error!.ShouldContain("item-id");
    }

    [Fact]
    public async Task PrNumberInvalid_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 5678, prNumber: 0));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("config_error");
        result.Error.ShouldNotBeNull(); result.Error!.ShouldContain("pr-number");
    }

    [Fact]
    public async Task ParentIdEqualsItem_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 5678, prNumber: 42, parentItemId: 5678));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("config_error");
    }

    // ─── Lock contention ────────────────────────────────────────────────

    [Fact]
    public async Task LockHeld_ReturnsLockHeldError()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);

        // Pre-create the lock file so TryAcquire returns Held.
        var lockDir = Path.Combine(_tempDir, ".polyphony", "locks");
        Directory.CreateDirectory(lockDir);
        var lockFile = Path.Combine(lockDir, "run-100.lock");
        File.WriteAllText(lockFile,
            "schema: 1\nroot_id: 100\nlock_token: existing\nacquired_by: someone\nacquired_at: 2026-05-06T00:00:00Z\nttl_until: 2099-01-01T00:00:00Z\n");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("lock_held");
    }

    // ─── Branch derivation ──────────────────────────────────────────────

    [Fact]
    public async Task RootPlan_HeadIsRootPlanBranch()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubFetch(runner, "feature/100");

        // Simulate dirty worktree to short-circuit before the merge.
        StubStatusDirty(runner);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));
        var result = Parse(output);

        // Even though it errored on dirty worktree, the verb populates head/base before erroring.
        result.HeadBranch.ShouldBe("plan/100");
        result.BaseBranch.ShouldBe("feature/100");
        result.IsRootPlan.ShouldBeTrue();
        result.ItemKey.ShouldBe("root");
    }

    [Fact]
    public async Task DescendantPlan_HeadIsDescendantBranch_BaseIsRootPlan()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusDirty(runner);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 5678, prNumber: 42, manifestPath: _manifestPath));
        var result = Parse(output);

        result.HeadBranch.ShouldBe("plan/100-5678");
        result.BaseBranch.ShouldBe("plan/100");
        result.IsRootPlan.ShouldBeFalse();
        result.ItemKey.ShouldBe("5678");
    }

    [Fact]
    public async Task DescendantPlan_WithExplicitParent_BaseIsParentPlan()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusDirty(runner);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 99999, prNumber: 42, parentItemId: 5678, manifestPath: _manifestPath));
        var result = Parse(output);

        result.HeadBranch.ShouldBe("plan/100-99999");
        result.BaseBranch.ShouldBe("plan/100-5678");
        result.ParentItemId.ShouldBe(5678);
    }

    // ─── Worktree dirty refusal ─────────────────────────────────────────

    [Fact]
    public async Task WorktreeDirty_ReturnsWorktreeDirty()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusDirty(runner);
        SeedManifest(100);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("worktree_dirty");
        result.Error.ShouldNotBeNull(); result.Error!.ShouldContain("not clean");
        // Manifest was not mutated.
        LoadManifest().PlanGenerations.ShouldBeEmpty();
    }

    // ─── PR identity validation ─────────────────────────────────────────

    [Fact]
    public async Task HeadRefMismatch_ReturnsHeadRefMismatch()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "OPEN", headRefName: "wrong-head", baseRefName: "feature/100");
        SeedManifest(100);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("head_ref_mismatch");
        result.Error.ShouldNotBeNull(); result.Error!.ShouldContain("wrong-head");
        result.Merged.ShouldBeFalse();
    }

    [Fact]
    public async Task BaseRefMismatch_ReturnsBaseRefMismatch()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "OPEN", headRefName: "plan/100", baseRefName: "wrong-base");
        SeedManifest(100);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("base_ref_mismatch");
    }

    // ─── PR state branching ─────────────────────────────────────────────

    [Fact]
    public async Task PrClosedNotMerged_ReturnsUnmergeable()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "CLOSED", headRefName: "plan/100", baseRefName: "feature/100");
        SeedManifest(100);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_state_unmergeable");
        result.PrState.ShouldBe("CLOSED");
    }

    [Fact]
    public async Task PrAlreadyMerged_NoLedgerEntry_RecordsAndPushesManifest()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "MERGED",
            headRefName: "plan/100",
            baseRefName: "feature/100",
            mergeCommitSha: "deadbeef",
            mergedAt: "2026-05-06T12:00:00Z");
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        StubAdd(runner, _manifestPath);
        StubCommit(runner);
        StubPush(runner, "feature/100");
        SeedManifest(100);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("");
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("deadbeef");
        result.ManifestRecorded.ShouldBeTrue();
        result.ManifestPushed.ShouldBeTrue();
        result.PreviousGeneration.ShouldBe(0);
        result.CurrentGeneration.ShouldBe(1);

        // Ledger now has the entry.
        var manifest = LoadManifest();
        manifest.MergedPlanPrs.Count.ShouldBe(1);
        manifest.MergedPlanPrs[0].PrNumber.ShouldBe(42);
        manifest.MergedPlanPrs[0].MergeCommit.ShouldBe("deadbeef");
        manifest.MergedPlanPrs[0].ItemKey.ShouldBe("root");
        manifest.PlanGenerations["root"].ShouldBe(1);
    }

    [Fact]
    public async Task PrAlreadyMerged_LedgerHasMatchingEntry_IdempotentNoOp()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "MERGED",
            headRefName: "plan/100",
            baseRefName: "feature/100",
            mergeCommitSha: "deadbeef",
            mergedAt: "2026-05-06T12:00:00Z");
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        SeedManifest(100,
            planGenerations: new Dictionary<string, int>(StringComparer.Ordinal) { ["root"] = 1 },
            ledger: new List<MergedPlanPrEntry>
            {
                new()
                {
                    PrNumber = 42,
                    ItemKey = "root",
                    MergeCommit = "deadbeef",
                    PreviousGeneration = 0,
                    CurrentGeneration = 1,
                    RecordedAt = DateTime.UtcNow.AddMinutes(-5),
                },
            });

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("");
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("deadbeef");
        // Idempotent — no fresh recording, no push.
        result.ManifestRecorded.ShouldBeFalse();
        result.ManifestPushed.ShouldBeFalse();
        result.PreviousGeneration.ShouldBe(0);
        result.CurrentGeneration.ShouldBe(1);

        // Manifest unchanged: still exactly 1 ledger entry, generation still 1.
        var manifest = LoadManifest();
        manifest.MergedPlanPrs.Count.ShouldBe(1);
        manifest.PlanGenerations["root"].ShouldBe(1);
    }

    [Fact]
    public async Task PrAlreadyMerged_MissingMergeCommitSha_ReturnsMissingMergeCommit()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "MERGED",
            headRefName: "plan/100",
            baseRefName: "feature/100",
            mergeCommitSha: null,  // platform omitted it
            mergedAt: "2026-05-06T12:00:00Z");
        SeedManifest(100);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("missing_merge_commit");
    }

    [Fact]
    public async Task PrAlreadyMerged_LedgerConflictOnDifferentItem_ReturnsLedgerConflict()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "MERGED",
            headRefName: "plan/100",
            baseRefName: "feature/100",
            mergeCommitSha: "deadbeef",
            mergedAt: "2026-05-06T12:00:00Z");
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        SeedManifest(100,
            ledger: new List<MergedPlanPrEntry>
            {
                new()
                {
                    PrNumber = 42,
                    ItemKey = "5678",  // different
                    MergeCommit = "deadbeef",
                    PreviousGeneration = 0,
                    CurrentGeneration = 1,
                    RecordedAt = DateTime.UtcNow.AddMinutes(-5),
                },
            });

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("ledger_conflict");
        result.Error.ShouldNotBeNull(); result.Error!.ShouldContain("'5678'");
    }
}
