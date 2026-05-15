using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
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
/// End-to-end tests for <c>polyphony plan recreate-stale-descendant</c>.
/// The verb is the second remedy policy of the Phase 3 P9 cascade-remedy:
/// when auto-rebase is not policy-applicable, this verb closes the stale
/// PR, deletes the head branch (best-effort), re-creates the plan branch
/// from the current parent-plan tip, opens a fresh PR with an up-to-date
/// snapshot, and records the recreation in the manifest's rebase ledger.
///
/// <para>These tests stub every shell-out (git fetch/show/status/checkout/
/// reset/push/...; gh pr view/list/close/create) via <see cref="FakeProcessRunner"/>
/// and assert on the JSON envelope plus side effects on the local manifest
/// and run lock.</para>
///
/// <para>Each test gets a fresh temp dir holding its own <c>run.yaml</c>
/// + <c>.polyphony/locks/</c>; the runner is wired so
/// <c>git rev-parse --show-toplevel</c> resolves to that temp dir.</para>
/// </summary>
public sealed class PlanCommandsRecreateStaleDescendantTests : CommandTestBase, IDisposable
{
    private const int RootId = 100;
    private const int ParentId = 200;
    private const int ItemId = 300;
    private const int PrNumber = 42;
    private const int NewPrNumber = 88;
    private const string HeadBranch = "plan/100-300";
    private const string ParentPlanBranch = "plan/100-200";
    private const string FeatureBranch = "feature/100";

    private const string OldHeadSha = "aaaaaaa1111111111111111111111111aaaaaaaa";

    private readonly string _tempDir;
    private readonly string _manifestPath;

    public PlanCommandsRecreateStaleDescendantTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyphony-recreate-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "run.yaml");
    }

    void IDisposable.Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        base.Dispose();
    }

    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner), new ThrowingAdoClient(), new FakePostconditionVerifier(), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(new GitClient(runner)), new Polyphony.Sdlc.Observers.RepoIdentityResolver(new GitClient(runner))), runner);
    }

    private static PlanRecreateStaleDescendantResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanRecreateStaleDescendantResult)!;

    private void SeedManifest(
        Dictionary<string, int>? planGenerations = null,
        List<RebaseRecord>? rebases = null)
    {
        var manifest = new RunManifest
        {
            Schema = 1,
            RootId = RootId,
            PlatformProject = "github.com/owner/repo",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = "test",
            BranchModelVersion = 1,
            PlanGenerations = planGenerations ?? new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["root"] = 2,
                ["200"] = 1,
            },
            Rebases = rebases ?? new List<RebaseRecord>(),
        };
        RunManifestStore.Save(_manifestPath, manifest);
    }

    private string ReadManifestYaml() => File.ReadAllText(_manifestPath);

    // ── Stubs ────────────────────────────────────────────────────────────

    private void StubEnvironmentDefaults(FakeProcessRunner runner, string? remote = null)
    {
        runner.WhenExact("git", ["rev-parse", "--show-toplevel"], new ProcessResult(0, _tempDir + "\n", ""));
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, Path.Combine(_tempDir, ".git") + "\n", ""));
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, (remote ?? "https://github.com/owner/repo.git") + "\n", ""));
    }

    private static void StubStatusClean(FakeProcessRunner runner)
        => runner.WhenExact("git", ["status", "--porcelain"], new ProcessResult(0, "", ""));

    private static void StubStatusDirty(FakeProcessRunner runner)
        => runner.WhenExact("git", ["status", "--porcelain"], new ProcessResult(0, " M file.txt\n", ""));

    private static void StubFetch(FakeProcessRunner runner, string branch, ProcessResult? result = null)
        => runner.WhenExact("git", ["fetch", "origin", branch], result ?? new ProcessResult(0, "", ""));

    private void StubShowManifest(FakeProcessRunner runner, string branch = FeatureBranch, string? yamlOverride = null, bool missing = false)
    {
        _ = runner;
        _ = branch;
        if (missing)
        {
            if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
            return;
        }
        if (yamlOverride is not null)
        {
            File.WriteAllText(_manifestPath, yamlOverride);
        }
        // else: leave the file as SeedManifest left it.
    }

    private static void StubPrPoll(
        FakeProcessRunner runner,
        int prNumber,
        string state,
        string headRefName,
        string baseRefName,
        string headRefOid,
        string body)
    {
        var bodyEsc = JsonEncodedText.Encode(body).Value;
        var json = $$"""
            {
              "number": {{prNumber}},
              "state": "{{state}}",
              "reviewDecision": "APPROVED",
              "mergeable": "MERGEABLE",
              "headRefName": "{{headRefName}}",
              "headRefOid": "{{headRefOid}}",
              "baseRefName": "{{baseRefName}}",
              "mergedAt": null,
              "mergeCommit": null,
              "body": "{{bodyEsc}}",
              "reviews": []
            }
            """;
        runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()], new ProcessResult(0, json, ""));
    }

    private static void StubPrPollNotFound(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "view", prNumber.ToString()],
            new ProcessResult(1, "", $"GraphQL: Could not resolve to a PullRequest with the number of {prNumber}."));

    /// <summary>No PR open at parent → cascade considers parent fresh by default.</summary>
    private static void StubCascadeParentNoPr(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubCascadeParentFreshPr(FakeProcessRunner runner, int parentPrNumber, string parentBody)
    {
        var listJson = $$"""[{"number":{{parentPrNumber}},"headRefName":"{{ParentPlanBranch}}","url":"https://github.com/owner/repo/pull/{{parentPrNumber}}","mergedAt":null}]""";
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, listJson, ""));
        StubPrPoll(runner, parentPrNumber, "OPEN", ParentPlanBranch, "feature/100", "deadbee0000000000000000000000000deadbee0", parentBody);
    }

    private static void StubGhPrCloseOk(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "close", prNumber.ToString()], new ProcessResult(0, "", ""));

    private static void StubGhPrCloseFailed(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "close", prNumber.ToString()],
            new ProcessResult(1, "", "branch protection prevented close"));

    private static void StubDeleteBranchOk(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "origin", "--delete", branch], new ProcessResult(0, "", ""));

    private static void StubDeleteBranchFailed(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "origin", "--delete", branch],
            new ProcessResult(1, "", $"error: unable to delete '{branch}': remote ref does not exist"));

    private static void StubCheckoutTrackingOk(FakeProcessRunner runner, string branch)
    {
        // GitClient.CheckoutTrackingAsync → git checkout --track {remote}/{branch}
        // (or fallbacks). We stub the most common path.
        runner.WhenExact("git", ["checkout", "--track", $"origin/{branch}"], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));
    }

    private static void StubCreateBranchOk(FakeProcessRunner runner, string branch, string startPoint)
        => runner.WhenExact("git", ["checkout", "-b", branch, startPoint], new ProcessResult(0, "", ""));

    private static void StubCreateBranchFailed(FakeProcessRunner runner, string branch, string startPoint)
        => runner.WhenExact("git", ["checkout", "-b", branch, startPoint],
            new ProcessResult(128, "", $"fatal: could not create branch '{branch}'"));

    private static void StubPushBranchOk(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubGhPrCreateOk(FakeProcessRunner runner, string baseBranch, string headBranch, int returnedPrNumber)
    {
        runner.When(
            (e, a) => e == "gh"
                && a.Count >= 2 && a[0] == "pr" && a[1] == "create"
                && ArgValue(a, "--base") == baseBranch
                && ArgValue(a, "--head") == headBranch,
            new ProcessResult(0, $"https://github.com/owner/repo/pull/{returnedPrNumber}\n", ""));
    }

    private static void StubGhPrCreateFailed(FakeProcessRunner runner, string baseBranch, string headBranch)
    {
        runner.When(
            (e, a) => e == "gh"
                && a.Count >= 2 && a[0] == "pr" && a[1] == "create"
                && ArgValue(a, "--base") == baseBranch
                && ArgValue(a, "--head") == headBranch,
            new ProcessResult(1, "", "GraphQL: A pull request already exists"));
    }

    private static string? ArgValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal)) return args[i + 1];
        }
        return null;
    }

    private static void StubManifestPushOk(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["checkout", FeatureBranch], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["reset", "--hard", $"origin/{FeatureBranch}"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("git", ["add"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("git", ["commit"], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["push", "-u", "origin", FeatureBranch], new ProcessResult(0, "", ""));
    }

    private static void StubManifestPushRejected(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["checkout", FeatureBranch], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["reset", "--hard", $"origin/{FeatureBranch}"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("git", ["add"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("git", ["commit"], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["push", "-u", "origin", FeatureBranch],
            new ProcessResult(1, "", " ! [rejected]        feature/100 -> feature/100 (non-fast-forward)"));
    }

    /// <summary>Builds a plan-PR body with YAML front-matter carrying an ancestor_plan_generations snapshot.</summary>
    private static string MakeBodyWithSnapshot(IDictionary<string, int> snapshot, bool requestsParentChange = false, string tail = "\n\nPlan PR body.")
    {
        var lines = new List<string>
        {
            "---",
            $"requests_parent_change: {(requestsParentChange ? "true" : "false")}",
            "ancestor_plan_generations:",
        };
        foreach (var (key, value) in snapshot)
        {
            lines.Add($"  \"{key}\": {value}");
        }
        lines.Add("---");
        return string.Join("\n", lines) + tail;
    }

    /// <summary>
    /// Stub the FULL happy-path chain for a successful recreate. Seeds the
    /// manifest, all git/gh calls.
    /// </summary>
    private void StubHappyPath(FakeProcessRunner runner, Dictionary<string, int>? snapshotInBody = null)
    {
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(snapshotInBody ?? new Dictionary<string, int>
        {
            ["root"] = 1,
            ["200"] = 0,
        });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubCascadeParentNoPr(runner);
        StubGhPrCloseOk(runner, PrNumber);
        StubDeleteBranchOk(runner, HeadBranch);
        StubCheckoutTrackingOk(runner, ParentPlanBranch);
        StubCreateBranchOk(runner, HeadBranch, ParentPlanBranch);
        StubPushBranchOk(runner, HeadBranch);
        StubGhPrCreateOk(runner, ParentPlanBranch, HeadBranch, NewPrNumber);
        StubManifestPushOk(runner);
    }

    // ════════════════════════════════════════════════════════════════════
    // 1. Validation
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RootIdInvalid_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(0, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Outcome.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("root-id");
    }

    [Fact]
    public async Task ItemIdInvalid_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, 0, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task ParentIdInvalid_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, 0, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task PrNumberInvalid_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, 0, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task ItemIdEqualsRootId_RefusesAsRootPlanRecreateOutOfScope()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, RootId, ParentId, PrNumber, manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("does not handle root-plan");
    }

    [Fact]
    public async Task ParentEqualsItem_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ItemId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task NoOriginRemote_ReturnsNoSlug()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["rev-parse", "--show-toplevel"], new ProcessResult(0, _tempDir + "\n", ""));
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(1, "", "fatal: No such remote 'origin'"));

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("no_slug");
    }

    // ════════════════════════════════════════════════════════════════════
    // 2. Parent-plan-branch derivation
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DirectChildOfRoot_DerivesParentBranchAsRootPlan()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest(planGenerations: new Dictionary<string, int>(StringComparer.Ordinal) { ["root"] = 2 });
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, "plan/100");
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, "plan/100", OldHeadSha, body);
        StubCascadeParentNoPr(runner);
        StubGhPrCloseOk(runner, PrNumber);
        StubDeleteBranchOk(runner, HeadBranch);
        StubCheckoutTrackingOk(runner, "plan/100");
        StubCreateBranchOk(runner, HeadBranch, "plan/100");
        StubPushBranchOk(runner, HeadBranch);
        StubGhPrCreateOk(runner, "plan/100", HeadBranch, NewPrNumber);
        StubManifestPushOk(runner);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, RootId, PrNumber, ancestorIds: "root", manifestPath: _manifestPath));

        var result = Parse(output);
        result.ParentPlanBranch.ShouldBe("plan/100");
        result.Outcome.ShouldBe("recreated");
    }

    [Fact]
    public async Task DeepDescendant_DerivesParentBranchAsDescendantPlan()
    {
        var (cmd, runner) = CreateCommand();
        StubHappyPath(runner);
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).ParentPlanBranch.ShouldBe(ParentPlanBranch);
    }

    // ════════════════════════════════════════════════════════════════════
    // 3. Lock + worktree
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LockHeld_ReturnsLockHeld()
    {
        var (cmd, runner) = CreateCommand();
        StubEnvironmentDefaults(runner);

        // Rev 4.2: lock lives at <git-common-dir>/polyphony/<root_id>/locks/run.lock.
        var lockDir = Path.Combine(_tempDir, ".git", "polyphony", $"{RootId}", "locks");
        Directory.CreateDirectory(lockDir);
        File.WriteAllText(Path.Combine(lockDir, "run.lock"),
            $"schema: 1\nroot_id: {RootId}\nlock_token: held-by-other\nacquired_by: peer\nacquired_at: 2099-01-01T00:00:00Z\nttl_until: 2099-01-02T00:00:00Z\n");

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("lock_held");
    }

    [Fact]
    public async Task WorktreeDirty_ReturnsWorktreeDirty()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusDirty(runner);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("worktree_dirty");
    }

    [Fact]
    public async Task LockReleased_AfterSuccessfulRecreate()
    {
        var (cmd, runner) = CreateCommand();
        StubHappyPath(runner);

        await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        File.Exists(Path.Combine(_tempDir, ".git", "polyphony", $"{RootId}", "locks", "run.lock")).ShouldBeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    // 4. Manifest read
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ManifestMissingAtOrigin_ReturnsManifestReadFailed()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner, missing: true);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("manifest_read_failed");
    }

    [Fact]
    public async Task ManifestRootMismatch_ReturnsManifestInvalid()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        var differentManifest = new RunManifest
        {
            Schema = 1,
            RootId = 999,
            PlatformProject = "github.com/owner/repo",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = 1,
        };
        var altPath = Path.Combine(_tempDir, "alt.yaml");
        RunManifestStore.Save(altPath, differentManifest);
        var altYaml = File.ReadAllText(altPath);
        StubShowManifest(runner, yamlOverride: altYaml);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("manifest_invalid");
    }

    // ════════════════════════════════════════════════════════════════════
    // 5. PR poll path / identity
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PrNotFound_ReturnsPrNotFound()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        StubPrPollNotFound(runner, PrNumber);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task PrHeadRefMismatch_ReturnsIdentityMismatch()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        StubPrPoll(runner, PrNumber, "OPEN", "wrong-branch", ParentPlanBranch, OldHeadSha,
            MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 }));

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_identity_mismatch");
    }

    [Fact]
    public async Task PrBaseRefMismatch_ReturnsIdentityMismatch()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, "main", OldHeadSha,
            MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 }));

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_identity_mismatch");
    }

    [Fact]
    public async Task PrClosedAndNoFreshReplacement_ReturnsPrStateInvalid()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        StubPrPoll(runner, PrNumber, "CLOSED", HeadBranch, ParentPlanBranch, OldHeadSha,
            MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 }));
        // Cascade list returns empty, AND replacement-PR list returns empty:
        // both queries hit the same `gh pr list` stub.
        StubCascadeParentNoPr(runner);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_state_invalid");
    }

    // ════════════════════════════════════════════════════════════════════
    // 6. Cascade precondition (parent stale)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ParentPrIsStale_ReturnsParentStale()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest(planGenerations: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["root"] = 3,  // current
            ["200"] = 2,
        });
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);

        // Parent has open PR with stale snapshot (root=2 < manifest's root=3)
        var parentBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2 });
        StubCascadeParentFreshPr(runner, parentPrNumber: 41, parentBody);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("parent_stale");
    }

    [Fact]
    public async Task ParentPrFresh_ProceedsToRecreate()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();  // root=2, 200=1
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);

        // Parent has open PR but its snapshot matches manifest → fresh
        var parentBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2 });
        StubCascadeParentFreshPr(runner, parentPrNumber: 41, parentBody);

        StubGhPrCloseOk(runner, PrNumber);
        StubDeleteBranchOk(runner, HeadBranch);
        StubCheckoutTrackingOk(runner, ParentPlanBranch);
        StubCreateBranchOk(runner, HeadBranch, ParentPlanBranch);
        StubPushBranchOk(runner, HeadBranch);
        StubGhPrCreateOk(runner, ParentPlanBranch, HeadBranch, NewPrNumber);
        StubManifestPushOk(runner);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).Outcome.ShouldBe("recreated");
    }

    // ════════════════════════════════════════════════════════════════════
    // 7. Happy path
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullHappyPath_ReturnsRecreated_AllStagesTrue()
    {
        var (cmd, runner) = CreateCommand();
        StubHappyPath(runner);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        var result = Parse(output);
        result.Outcome.ShouldBe("recreated");
        result.OldPrNumber.ShouldBe(PrNumber);
        result.NewPrNumber.ShouldBe(NewPrNumber);
        result.NewPrUrl!.ShouldContain($"/pull/{NewPrNumber}");
        result.NewHeadBranch.ShouldBe(HeadBranch);
        result.OldPrClosed.ShouldBeTrue();
        result.OldBranchDeleted.ShouldBeTrue();
        result.NewBranchCreated.ShouldBeTrue();
        result.NewPrOpened.ShouldBeTrue();
        result.ManifestRecorded.ShouldBeTrue();
        result.ManifestPushed.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task BranchDeleteFails_StillRecreates_WithWarning()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubCascadeParentNoPr(runner);
        StubGhPrCloseOk(runner, PrNumber);
        StubDeleteBranchFailed(runner, HeadBranch);  // delete fails — should be a warning
        StubCheckoutTrackingOk(runner, ParentPlanBranch);
        StubCreateBranchOk(runner, HeadBranch, ParentPlanBranch);
        StubPushBranchOk(runner, HeadBranch);
        StubGhPrCreateOk(runner, ParentPlanBranch, HeadBranch, NewPrNumber);
        StubManifestPushOk(runner);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        var result = Parse(output);
        result.Outcome.ShouldBe("recreated");
        result.OldBranchDeleted.ShouldBeFalse();
        result.NewBranchCreated.ShouldBeTrue();
        result.NewPrOpened.ShouldBeTrue();
        result.Warnings.ShouldNotBeEmpty();
        result.Warnings[0].ShouldContain("Old branch delete failed");
    }

    // ════════════════════════════════════════════════════════════════════
    // 8. Partial outcomes
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClosePrFails_ReturnsPrCloseFailed()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubCascadeParentNoPr(runner);
        StubGhPrCloseFailed(runner, PrNumber);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_close_failed");
        result.OldPrClosed.ShouldBeFalse();
        result.NewPrOpened.ShouldBeFalse();
    }

    [Fact]
    public async Task BranchCreateFails_ReturnsBranchCreateFailed_OldClosed()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubCascadeParentNoPr(runner);
        StubGhPrCloseOk(runner, PrNumber);
        StubDeleteBranchOk(runner, HeadBranch);
        StubCheckoutTrackingOk(runner, ParentPlanBranch);
        StubCreateBranchFailed(runner, HeadBranch, ParentPlanBranch);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("branch_create_failed");
        result.OldPrClosed.ShouldBeTrue();
        result.OldBranchDeleted.ShouldBeTrue();
        result.NewBranchCreated.ShouldBeFalse();
        result.NewPrOpened.ShouldBeFalse();
    }

    [Fact]
    public async Task PrCreateFails_ReturnsPrOpenFailed_BranchAlreadyCreated()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubCascadeParentNoPr(runner);
        StubGhPrCloseOk(runner, PrNumber);
        StubDeleteBranchOk(runner, HeadBranch);
        StubCheckoutTrackingOk(runner, ParentPlanBranch);
        StubCreateBranchOk(runner, HeadBranch, ParentPlanBranch);
        StubPushBranchOk(runner, HeadBranch);
        StubGhPrCreateFailed(runner, ParentPlanBranch, HeadBranch);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_open_failed");
        result.OldPrClosed.ShouldBeTrue();
        result.NewBranchCreated.ShouldBeTrue();
        result.NewPrOpened.ShouldBeFalse();
    }

    // ManifestPushRejected_ReturnsManifestPushRejected_NewPrOpened DELETED — Rev 4.2 dropped
    // the worktree-side manifest git transaction entirely. There is no longer any push to be rejected.

    // ════════════════════════════════════════════════════════════════════
    // 9. Three-fact noop / replay
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllThreeFactsHold_ReturnsNoop()
    {
        // Replay scenario: old PR is CLOSED, a fresh PR exists on the same
        // head with the manifest's current snapshot, and the rebase ledger
        // has a matching entry. Should detect and return noop without
        // touching close/branch/create.
        var (cmd, runner) = CreateCommand();
        SeedManifest(rebases: new List<RebaseRecord>
        {
            new() { Branch = HeadBranch, Onto = $"refs/heads/{ParentPlanBranch}", Reason = "child_plan_drift", Commit = OldHeadSha, RecordedAt = DateTime.UtcNow }
        });
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);

        // Old PR is already CLOSED with stale body — that's fine, the noop
        // path checks for the existing fresh replacement separately.
        var oldBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "CLOSED", HeadBranch, ParentPlanBranch, OldHeadSha, oldBody);

        // gh pr list is called for both cascade-parent (no PR) AND
        // fresh-replacement (one entry). Stub a sequence by matching on
        // the head filter — replacement query has --head=plan/100-300,
        // cascade query has --head=plan/100-200.
        runner.When(
            (e, a) => e == "gh" && a.Count >= 2 && a[0] == "pr" && a[1] == "list"
                && ArgValue(a, "--head") == HeadBranch,
            new ProcessResult(0, $$"""[{"number":{{NewPrNumber}},"headRefName":"{{HeadBranch}}","url":"https://github.com/owner/repo/pull/{{NewPrNumber}}","mergedAt":null}]""", ""));
        runner.When(
            (e, a) => e == "gh" && a.Count >= 2 && a[0] == "pr" && a[1] == "list"
                && ArgValue(a, "--head") == ParentPlanBranch,
            new ProcessResult(0, "[]", ""));

        // Fresh replacement PR's body matches the manifest snapshot.
        var freshBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2, ["200"] = 1 });
        StubPrPoll(runner, NewPrNumber, "OPEN", HeadBranch, ParentPlanBranch, "ffffffff0000000000000000000000000ffffffff", freshBody);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        var result = Parse(output);
        result.Outcome.ShouldBe("noop");
        result.NewPrNumber.ShouldBe(NewPrNumber);
        result.OldPrClosed.ShouldBeTrue();
    }

    [Fact]
    public async Task OldPrClosedButNoFreshReplacement_FallsThroughToPrStateInvalid()
    {
        // Three-fact noop fails because there's no fresh replacement —
        // verb refuses to act on a CLOSED old PR with no replacement,
        // returns pr_state_invalid.
        var (cmd, runner) = CreateCommand();
        SeedManifest(rebases: new List<RebaseRecord>
        {
            new() { Branch = HeadBranch, Onto = $"refs/heads/{ParentPlanBranch}", Reason = "child_plan_drift", Commit = OldHeadSha, RecordedAt = DateTime.UtcNow }
        });
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner);
        var oldBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "CLOSED", HeadBranch, ParentPlanBranch, OldHeadSha, oldBody);
        StubCascadeParentNoPr(runner);  // covers both cascade and replacement queries

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RecreateStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        Parse(output).ErrorCode.ShouldBe("pr_state_invalid");
    }
}
