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
        return (new PrCommands(git, gh, twig, Repository, Config, new RunLockStore(), new RunLockPathResolver(git), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git)), runner);
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
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, Path.Combine(_tempDir, ".git") + "\n", ""));
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
        string mergedAt = "",
        string body = "")
    {
        var mergeCommitClause = mergeCommitSha is null ? "null" : $$"""{"oid":"{{mergeCommitSha}}"}""";
        var mergedAtClause = string.IsNullOrEmpty(mergedAt) ? "null" : $"\"{mergedAt}\"";
        var bodyClause = JsonEncodedText.Encode(body).Value;
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
              "body": "{{bodyClause}}",
              "reviews": []
            }
            """;
        // poll-status uses a wide --json field set
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    /// <summary>
    /// Rev 4.2: manifest is read from local disk. Seeds the local file at
    /// <see cref="_manifestPath"/> with <paramref name="yamlContent"/>, or
    /// deletes it when <paramref name="missing"/> is true. The
    /// <paramref name="branch"/> parameter is retained for call-site
    /// compatibility but no longer participates.
    /// </summary>
    private void StubGitShowManifest(FakeProcessRunner runner, string branch, string yamlContent, bool missing = false)
    {
        _ = runner;
        _ = branch;
        if (missing)
        {
            if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
        }
        else
        {
            File.WriteAllText(_manifestPath, yamlContent);
        }
    }

    /// <summary>Builds a plan-PR body with YAML front-matter carrying an ancestor_plan_generations snapshot.</summary>
    private static string MakeBodyWithSnapshot(IDictionary<string, int> snapshot, bool requestsParentChange = false)
    {
        var lines = new List<string>
        {
            "---",
            $"requests_parent_change: {(requestsParentChange ? "true" : "false")}",
            "ancestor_plan_generations:",
        };
        foreach (var (key, value) in snapshot)
        {
            // quote the key so numeric-string keys survive YAML round-trip cleanly
            lines.Add($"  \"{key}\": {value}");
        }
        lines.Add("---");
        lines.Add("");
        lines.Add("Plan PR body.");
        return string.Join("\n", lines);
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
        // Rev 4.2: lock lives at <git-common-dir>/polyphony/<root_id>/locks/run.lock.
        var lockDir = Path.Combine(_tempDir, ".git", "polyphony", "100", "locks");
        Directory.CreateDirectory(lockDir);
        var lockFile = Path.Combine(lockDir, "run.lock");
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

    // ─── P6 stale-generation merge block ────────────────────────────────

    /// <summary>
    /// OPEN descendant plan PR whose body snapshot says the root ancestor
    /// was at generation 1 when the PR was opened, but the manifest now
    /// records generation 3 (someone merged 2 newer root-plan PRs while
    /// this PR was open). Must refuse with <c>stale_generation</c> and
    /// surface the diff in <see cref="PrMergePlanPrResult.StaleAncestors"/>.
    /// </summary>
    [Fact]
    public async Task OpenDescendant_StaleSnapshot_BlocksWithStaleGeneration()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");

        var snapshot = new Dictionary<string, int> { ["root"] = 1 };
        StubPrPoll(runner, 42, "OPEN",
            headRefName: "plan/100-1100", baseRefName: "plan/100",
            body: MakeBodyWithSnapshot(snapshot));

        // Manifest on the feature branch has root at generation 3 — newer than the snapshot.
        var manifestYaml = """
            schema: 1
            root_id: 100
            platform_project: github.com/owner/repo
            created_at: 2026-05-06T00:00:00Z
            created_by: test
            branch_model_version: 1
            plan_generations:
              root: 3
            merged_plan_prs: []
            """;
        StubGitShowManifest(runner, "feature/100", manifestYaml);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 1100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("stale_generation");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("root: snapshot=1, current=3");
        result.StaleAncestors.ShouldNotBeNull();
        result.StaleAncestors!.Count.ShouldBe(1);
        result.StaleAncestors[0].AncestorKey.ShouldBe("root");
        result.StaleAncestors[0].SnapshotGeneration.ShouldBe(1);
        result.StaleAncestors[0].CurrentGeneration.ShouldBe(3);
        // No merge attempted, no ledger touched.
        result.Merged.ShouldBeFalse();
        result.ManifestRecorded.ShouldBeFalse();
    }

    /// <summary>
    /// OPEN descendant plan PR with no front-matter at all (e.g. hand-opened
    /// or pre-P3) cannot be safely merged — we have no way to verify it
    /// was opened against the current ancestor state. Refuse with a clear
    /// diagnostic pointing at <c>polyphony pr open-plan-pr</c>.
    /// </summary>
    [Fact]
    public async Task OpenDescendant_EmptyBodyNoSnapshot_BlocksWithStaleGeneration()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "OPEN",
            headRefName: "plan/100-1100", baseRefName: "plan/100",
            body: "");  // no front-matter

        var manifestYaml = """
            schema: 1
            root_id: 100
            platform_project: github.com/owner/repo
            created_at: 2026-05-06T00:00:00Z
            created_by: test
            branch_model_version: 1
            plan_generations:
              root: 1
            merged_plan_prs: []
            """;
        StubGitShowManifest(runner, "feature/100", manifestYaml);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 1100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("stale_generation");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("no ancestor_plan_generations snapshot");
        result.Error.ShouldContain("polyphony pr open-plan-pr");
        result.StaleAncestors.ShouldBeNull();  // empty case has no diff
        result.Merged.ShouldBeFalse();
        result.ManifestRecorded.ShouldBeFalse();
    }

    /// <summary>
    /// MERGED PR with a snapshot that WOULD be stale if checked. The
    /// staleness check is a pre-merge guard — once the platform records
    /// the merge, we're in recovery mode and the only honest action is
    /// to record the merge in the ledger. The stale snapshot is a
    /// post-mortem signal for the operator, not a reason to refuse the
    /// recovery path.
    /// </summary>
    [Fact]
    public async Task MergedPr_StaleSnapshot_CheckSkipped_ProceedsToRecovery()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");

        var snapshot = new Dictionary<string, int> { ["root"] = 1 };
        StubPrPoll(runner, 42, "MERGED",
            headRefName: "plan/100-1100", baseRefName: "plan/100",
            mergeCommitSha: "deadbeef",
            mergedAt: "2026-05-06T12:00:00Z",
            body: MakeBodyWithSnapshot(snapshot));

        // No StubGitShowManifest — the staleness check should never run for MERGED PRs,
        // so an unstubbed `git show` would crash if it did. This is the assertion.
        StubCheckout(runner, "feature/100");
        StubResetHard(runner, "origin/feature/100");
        StubAdd(runner, _manifestPath);
        StubCommit(runner);
        StubPush(runner, "feature/100");
        SeedManifest(100,
            planGenerations: new Dictionary<string, int>(StringComparer.Ordinal) { ["root"] = 5 });  // very stale

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 1100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("");
        result.Merged.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeCommit.ShouldBe("deadbeef");
        result.ManifestRecorded.ShouldBeTrue();
        result.ManifestPushed.ShouldBeTrue();
    }

    /// <summary>
    /// Root plan PR with empty body proceeds past the staleness gate
    /// (root has no ancestors, so an empty snapshot is correct by design).
    /// We assert by setting up a MERGED-state recovery — the staleness
    /// check would have refused if it ran, but for root plans it's
    /// skipped entirely.
    /// </summary>
    [Fact]
    public async Task RootPlan_EmptyBody_SkipsStalenessCheck()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");
        StubPrPoll(runner, 42, "MERGED",
            headRefName: "plan/100", baseRefName: "feature/100",
            mergeCommitSha: "deadbeef",
            mergedAt: "2026-05-06T12:00:00Z",
            body: "");

        // No StubGitShowManifest — should never be called (root + MERGED both skip the check).
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
        result.IsRootPlan.ShouldBeTrue();
        result.Merged.ShouldBeTrue();
    }

    /// <summary>
    /// OPEN descendant plan PR whose snapshot matches the manifest exactly
    /// — staleness check passes silently and the verb proceeds to the merge
    /// step. We don't stub <c>gh pr merge</c>; the verb catches the
    /// "no responder" exception and emits <c>merge_failed</c>. Seeing
    /// that error code (instead of <c>stale_generation</c>) proves the
    /// staleness check did NOT refuse the merge.
    /// </summary>
    [Fact]
    public async Task OpenDescendant_FreshSnapshot_PassesStalenessCheck()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");

        var snapshot = new Dictionary<string, int> { ["root"] = 2 };
        StubPrPoll(runner, 42, "OPEN",
            headRefName: "plan/100-1100", baseRefName: "plan/100",
            body: MakeBodyWithSnapshot(snapshot));

        var manifestYaml = """
            schema: 1
            root_id: 100
            platform_project: github.com/owner/repo
            created_at: 2026-05-06T00:00:00Z
            created_by: test
            branch_model_version: 1
            plan_generations:
              root: 2
            merged_plan_prs: []
            """;
        StubGitShowManifest(runner, "feature/100", manifestYaml);

        // Deliberately NO `gh pr merge` stub. The verb catches the
        // "no responder" exception from FakeProcessRunner and surfaces it
        // as `merge_failed` (not `stale_generation` — the assertion).
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 1100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("merge_failed");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("gh pr merge");
        // StaleAncestors must be null — proves the staleness check did NOT fire.
        result.StaleAncestors.ShouldBeNull();
    }

    // ─── P8b validator-guard wiring ─────────────────────────────────────

    /// <summary>
    /// Stub for <see cref="IGhClient.GetPullRequestFilesAsync"/>. Registered
    /// BEFORE <see cref="StubPrPoll"/> so the more-specific matcher (for
    /// <c>--json files</c>) wins over the generic poll matcher.
    /// </summary>
    private static void StubPrFiles(FakeProcessRunner runner, int prNumber, params string[] paths)
    {
        var json = "{\"files\":[" + string.Join(",", paths.Select(p =>
            $"{{\"path\":\"{p}\",\"additions\":1,\"deletions\":0}}")) + "]}";
        runner.When(
            (exe, args) => exe == "gh"
                && args.Count >= 4
                && args[0] == "pr" && args[1] == "view" && args[2] == prNumber.ToString()
                && args.Contains("files"),
            new ProcessResult(0, json, ""));
    }

    [Fact]
    public async Task ValidationGuard_BlockingDiff_EmitsValidationBlocked()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");

        // Files-specific matcher MUST be registered before the generic
        // poll-status matcher in StubPrPoll, since first-match wins.
        // Touching .polyphony/ → Blocking, child_touched_polyphony_state.
        StubPrFiles(runner, 42, ".polyphony/run.yaml");

        var snapshot = new Dictionary<string, int> { ["root"] = 2 };
        StubPrPoll(runner, 42, "OPEN",
            headRefName: "plan/100-1100", baseRefName: "plan/100",
            body: MakeBodyWithSnapshot(snapshot));

        var manifestYaml = """
            schema: 1
            root_id: 100
            platform_project: github.com/owner/repo
            created_at: 2026-05-06T00:00:00Z
            created_by: test
            branch_model_version: 1
            plan_generations:
              root: 2
            merged_plan_prs: []
            """;
        StubGitShowManifest(runner, "feature/100", manifestYaml);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 1100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("validation_blocked");
        result.Error.ShouldNotBeNull();
        // The merge MUST NOT have been attempted; verify no `gh pr merge` in the recorded invocations.
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "gh" && i.Arguments.Count >= 2 && i.Arguments[0] == "pr" && i.Arguments[1] == "merge");
    }

    [Fact]
    public async Task ValidationGuard_OnlyOwnPlan_PassesThrough()
    {
        // Touching only the PR's own plan file is the OK case — clean diff.
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, "feature/100");

        StubPrFiles(runner, 42, "plans/plan-1100.md");

        var snapshot = new Dictionary<string, int> { ["root"] = 2 };
        StubPrPoll(runner, 42, "OPEN",
            headRefName: "plan/100-1100", baseRefName: "plan/100",
            body: MakeBodyWithSnapshot(snapshot));

        var manifestYaml = """
            schema: 1
            root_id: 100
            platform_project: github.com/owner/repo
            created_at: 2026-05-06T00:00:00Z
            created_by: test
            branch_model_version: 1
            plan_generations:
              root: 2
            merged_plan_prs: []
            """;
        StubGitShowManifest(runner, "feature/100", manifestYaml);

        // Don't stub `gh pr merge`; we want the verb to fail at the merge
        // step, NOT at the validator guard. That proves the guard let it through.
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.MergePlanPr(rootId: 100, itemId: 1100, prNumber: 42, manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("merge_failed");
        result.ErrorCode.ShouldNotBe("validation_blocked");
    }
}

