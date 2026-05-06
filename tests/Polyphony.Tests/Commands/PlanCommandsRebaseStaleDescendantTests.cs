using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony plan rebase-stale-descendant</c>. The
/// verb is a compound transactional remedy that auto-rebases stale
/// descendant plan PRs onto current parent-plan tips. These tests stub
/// every shell-out (git status/fetch/show/checkout/reset/rebase/push/etc.,
/// gh pr view/list/edit/comment) via <see cref="FakeProcessRunner"/>
/// and assert on the JSON envelope plus side effects on the local
/// manifest file and run lock.
///
/// <para>Each test gets a fresh temp dir holding its own
/// <c>run.yaml</c> + <c>.polyphony/locks/</c>. The runner is wired so
/// <c>git rev-parse --show-toplevel</c> resolves to that temp dir, which
/// makes the lock-path resolver place the lock there too.</para>
/// </summary>
public sealed class PlanCommandsRebaseStaleDescendantTests : CommandTestBase, IDisposable
{
    private const int RootId = 100;
    private const int ParentId = 200;
    private const int ItemId = 300;
    private const int PrNumber = 42;
    private const string HeadBranch = "plan/100-300";
    private const string ParentPlanBranch = "plan/100-200";
    private const string FeatureBranch = "feature/100";

    private const string OldHeadSha = "aaaaaaa1111111111111111111111111aaaaaaaa";
    private const string NewHeadSha = "bbbbbbb2222222222222222222222222bbbbbbbb";

    private readonly string _tempDir;
    private readonly string _manifestPath;

    public PlanCommandsRebaseStaleDescendantTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polyphony-rebase-stale-" + Guid.NewGuid().ToString("N"));
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
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner)), runner);
    }

    private static PlanRebaseStaleDescendantResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanRebaseStaleDescendantResult)!;

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
    private RunManifest LoadManifest() => RunManifestStore.LoadOrThrow(_manifestPath);

    // ── Stubs ────────────────────────────────────────────────────────────

    private void StubEnvironmentDefaults(FakeProcessRunner runner, string? remote = null)
    {
        runner.WhenExact("git", ["rev-parse", "--show-toplevel"], new ProcessResult(0, _tempDir + "\n", ""));
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
        var refspec = $"origin/{branch}:{_manifestPath}";
        if (missing)
        {
            runner.WhenExact("git", ["show", refspec],
                new ProcessResult(128, "", $"fatal: path '{_manifestPath}' does not exist in 'origin/{branch}'"));
            return;
        }
        var yaml = yamlOverride ?? ReadManifestYaml();
        runner.WhenExact("git", ["show", refspec], new ProcessResult(0, yaml, ""));
    }

    /// <summary>
    /// Stubs <c>gh pr view {prNumber} ...</c> with the wide JSON shape
    /// that <see cref="GhClient.GetPullRequestPollDataAsync"/> requests.
    /// </summary>
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

    private static void StubPrListNoneAtParent(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    /// <summary>
    /// Stub the WHOLE parent-PR cascade pre-check chain in one call:
    /// either there are no open PRs at the parent (no-PR fresh) OR the
    /// open PR's snapshot matches the manifest (snapshot fresh).
    /// </summary>
    private static void StubCascadeParentNoPr(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubCascadeParentFreshPr(FakeProcessRunner runner, int parentPrNumber, string parentBody)
    {
        var listJson = $$"""[{"number":{{parentPrNumber}},"headRefName":"{{ParentPlanBranch}}","url":"https://github.com/owner/repo/pull/{{parentPrNumber}}","mergedAt":null}]""";
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, listJson, ""));
        StubPrPoll(runner, parentPrNumber, "OPEN", ParentPlanBranch, "feature/100", "deadbee0000000000000000000000000deadbee0", parentBody);
    }

    private static void StubRevParseOriginHead(FakeProcessRunner runner, string branch, string sha)
    {
        // ResolveRefSilentlyAsync uses MergeBaseAsync(refspec, refspec) to resolve a SHA.
        runner.WhenExact("git", ["merge-base", $"origin/{branch}", $"origin/{branch}"],
            new ProcessResult(0, sha + "\n", ""));
    }

    private static void StubIsAncestor(FakeProcessRunner runner, string ancestor, string descendant, bool result)
    {
        var exit = result ? 0 : 1;
        runner.WhenExact("git", ["merge-base", "--is-ancestor", ancestor, descendant], new ProcessResult(exit, "", ""));
    }

    private static void StubMergeBase(FakeProcessRunner runner, string a, string b, string result)
        => runner.WhenExact("git", ["merge-base", a, b], new ProcessResult(0, result + "\n", ""));

    private static void StubRebaseClean(FakeProcessRunner runner, string upstream, string oldBase, string headSha, string newSha)
    {
        // GitClient.RebaseOntoAsync sequence: checkout --detach {head}, then
        // rebase --onto {upstream} {oldBase} HEAD, then rev-parse HEAD.
        runner.WhenExact("git", ["checkout", "--detach", headSha], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["rebase", "--onto", upstream, oldBase, "HEAD"], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["rev-parse", "HEAD"], new ProcessResult(0, newSha + "\n", ""));
    }

    private static void StubRebaseConflict(FakeProcessRunner runner, string upstream, string oldBase, string headSha, params string[] conflictedFiles)
    {
        runner.WhenExact("git", ["checkout", "--detach", headSha], new ProcessResult(0, "", ""));
        var stdout = "Applying: change\n" + string.Join('\n', conflictedFiles.Select(f => $"CONFLICT (content): Merge conflict in {f}")) + "\nerror: Failed to merge in the changes.\n";
        runner.WhenExact("git", ["rebase", "--onto", upstream, oldBase, "HEAD"], new ProcessResult(1, stdout, ""));
        runner.WhenExact("git", ["rebase", "--abort"], new ProcessResult(0, "", ""));
    }

    private static void StubRebaseFailed(FakeProcessRunner runner, string upstream, string oldBase, string headSha)
    {
        runner.WhenExact("git", ["checkout", "--detach", headSha], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["rebase", "--onto", upstream, oldBase, "HEAD"], new ProcessResult(1, "", "fatal: bad merge"));
        runner.WhenExact("git", ["rebase", "--abort"], new ProcessResult(0, "", ""));
    }

    private static void StubPushHeadWithLeaseSuccess(FakeProcessRunner runner, string branch, string expectedSha)
        => runner.WhenExact("git",
            ["push", "origin", $"HEAD:refs/heads/{branch}", $"--force-with-lease=refs/heads/{branch}:{expectedSha}"],
            new ProcessResult(0, "", ""));

    private static void StubPushHeadWithLeaseRejected(FakeProcessRunner runner, string branch, string expectedSha)
        => runner.WhenExact("git",
            ["push", "origin", $"HEAD:refs/heads/{branch}", $"--force-with-lease=refs/heads/{branch}:{expectedSha}"],
            new ProcessResult(1, "", "error: stale info: remote ref has changed; refusing to update"));

    private static void StubGhPrEditOk(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "edit", prNumber.ToString()], new ProcessResult(0, "", ""));

    private static void StubGhPrEditFailed(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "edit", prNumber.ToString()],
            new ProcessResult(1, "", "GraphQL: Resource not accessible by integration."));

    private static void StubGhPrCommentOk(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "comment", prNumber.ToString()], new ProcessResult(0, "", ""));

    private static void StubGhPrCommentFailed(FakeProcessRunner runner, int prNumber)
        => runner.WhenStartsWith("gh", ["pr", "comment", prNumber.ToString()],
            new ProcessResult(1, "", "comment-API down"));

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
    /// Stub the FULL happy-path chain (env, status, fetches, show, poll,
    /// cascade, rev-parse, ancestor, merge-base, rebase, push, edit,
    /// manifest push, comment). Returns the manifest seeded.
    /// </summary>
    private void StubHappyPath(
        FakeProcessRunner runner,
        Dictionary<string, int>? snapshotInBody = null)
    {
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(snapshotInBody ?? new Dictionary<string, int>
        {
            ["root"] = 1,
            ["200"] = 0,
        });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseClean(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseSuccess(runner, HeadBranch, OldHeadSha);
        StubGhPrEditOk(runner, PrNumber);
        StubManifestPushOk(runner);
        StubGhPrCommentOk(runner, PrNumber);
    }

    // ════════════════════════════════════════════════════════════════════
    // 1. Validation
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RootIdInvalid_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(0, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
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
            cmd.RebaseStaleDescendant(RootId, 0, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task ParentIdInvalid_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, 0, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task PrNumberInvalid_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, 0, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task ItemIdEqualsRootId_RefusesAsRootPlanRebaseOutOfScope()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, RootId, ParentId, PrNumber, manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error!.ShouldContain("does not handle root-plan");
    }

    [Fact]
    public async Task ParentEqualsItem_ReturnsInvalidArgument()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ItemId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task NoOriginRemote_ReturnsNoSlug()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["rev-parse", "--show-toplevel"], new ProcessResult(0, _tempDir + "\n", ""));
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(1, "", "fatal: No such remote 'origin'"));

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("no_slug");
    }

    // ════════════════════════════════════════════════════════════════════
    // 2. Parent-plan-branch derivation
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DirectChildOfRoot_DerivesParentBranchAsRootPlan()
    {
        // parentItemId == rootId → parent branch is plan/{root} (NOT feature/{root}).
        var (cmd, runner) = CreateCommand();
        SeedManifest(planGenerations: new Dictionary<string, int>(StringComparer.Ordinal) { ["root"] = 2 });
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, "plan/100");  // direct child → root plan branch
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, "plan/100", OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, "origin/plan/100", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, "origin/plan/100", "f00ba12c");
        StubRebaseClean(runner, "origin/plan/100", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseSuccess(runner, HeadBranch, OldHeadSha);
        StubGhPrEditOk(runner, PrNumber);
        StubManifestPushOk(runner);
        StubGhPrCommentOk(runner, PrNumber);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, RootId, PrNumber, ancestorIds: "root", manifestPath: _manifestPath));

        var result = Parse(output);
        result.ParentPlanBranch.ShouldBe("plan/100");
        result.Outcome.ShouldBe("rebased");
    }

    [Fact]
    public async Task DeepDescendant_DerivesParentBranchAsDescendantPlan()
    {
        var (cmd, runner) = CreateCommand();
        StubHappyPath(runner);
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
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

        var lockDir = Path.Combine(_tempDir, ".polyphony", "locks");
        Directory.CreateDirectory(lockDir);
        File.WriteAllText(Path.Combine(lockDir, $"run-{RootId}.lock"),
            $"schema: 1\nroot_id: {RootId}\nlock_token: held-by-other\nacquired_by: peer\nacquired_at: 2099-01-01T00:00:00Z\nttl_until: 2099-01-02T00:00:00Z\n");

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
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
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("worktree_dirty");
    }

    [Fact]
    public async Task LockReleased_AfterSuccessfulRebase()
    {
        var (cmd, runner) = CreateCommand();
        StubHappyPath(runner);

        var (_, _) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        File.Exists(Path.Combine(_tempDir, ".polyphony", "locks", $"run-{RootId}.lock")).ShouldBeFalse();
    }

    [Fact]
    public async Task LockReleased_AfterRebaseConflict()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseConflict(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, "src/foo.cs");

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        Parse(output).Outcome.ShouldBe("conflict");
        File.Exists(Path.Combine(_tempDir, ".polyphony", "locks", $"run-{RootId}.lock")).ShouldBeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    // 4. Manifest read
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ManifestMissingAtOrigin_ReturnsManifestReadFailed()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();  // local exists for path, but origin-show stub returns missing
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubShowManifest(runner, missing: true);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
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
        // Show a manifest with a DIFFERENT root_id
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
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("manifest_invalid");
    }

    // ════════════════════════════════════════════════════════════════════
    // 5. PR poll path
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
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_not_found");
    }

    [Fact]
    public async Task PrClosed_ReturnsPrStateInvalid()
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

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_state_invalid");
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
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
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
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_identity_mismatch");
    }

    [Fact]
    public async Task HeadMovedBetweenPollAndFetch_ReturnsPrHeadChanged()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha,
            MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 }));
        // origin/HEAD resolves to a DIFFERENT sha than the polled one.
        StubRevParseOriginHead(runner, HeadBranch, "ccccccc3333333333333333333333333ccccccc");

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_head_changed");
    }

    [Fact]
    public async Task LeaseRejected_ReturnsPrHeadChanged()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseClean(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseRejected(runner, HeadBranch, OldHeadSha);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("pr_head_changed");
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
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);

        // Parent has an open PR with stale snapshot (root=2 < manifest's root=3)
        var parentBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2 });
        StubCascadeParentFreshPr(runner, parentPrNumber: 41, parentBody);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("parent_stale");
    }

    [Fact]
    public async Task ParentPrFresh_ProceedsToRebase()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();  // root=2, 200=1
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);

        // Parent has open PR but its snapshot matches manifest → fresh
        var parentBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2 });
        StubCascadeParentFreshPr(runner, parentPrNumber: 41, parentBody);

        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseClean(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseSuccess(runner, HeadBranch, OldHeadSha);
        StubGhPrEditOk(runner, PrNumber);
        StubManifestPushOk(runner);
        StubGhPrCommentOk(runner, PrNumber);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).Outcome.ShouldBe("rebased");
    }

    // ════════════════════════════════════════════════════════════════════
    // 7. Three-fact noop / partial replay
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllThreeFactsFresh_ReturnsNoop()
    {
        var (cmd, runner) = CreateCommand();
        // Seed a manifest with a prior rebase entry matching (head, polledSha).
        SeedManifest(rebases: new List<RebaseRecord>
        {
            new() { Branch = HeadBranch, Onto = $"refs/heads/{ParentPlanBranch}", Reason = "child_plan_drift", Commit = OldHeadSha, RecordedAt = DateTime.UtcNow }
        });
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        // Body matches manifest's current snapshot exactly
        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2, ["200"] = 1 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", true);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        var result = Parse(output);
        result.Outcome.ShouldBe("noop");
        result.OldHeadSha.ShouldBe(OldHeadSha);
        result.NewHeadSha.ShouldBe(OldHeadSha);
    }

    [Fact]
    public async Task BranchFreshButLedgerMissing_RecordsAndPushesManifestOnly()
    {
        var (cmd, runner) = CreateCommand();
        // No prior rebase ledger entry.
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        // Body matches manifest's current snapshot — body fresh.
        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2, ["200"] = 1 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", true);
        // No rebase, no push-with-lease, no body edit. Just manifest push + comment skipped.
        StubManifestPushOk(runner);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        var result = Parse(output);
        result.Outcome.ShouldBe("rebased");
        result.NewHeadSha.ShouldBe(OldHeadSha);  // didn't actually rebase
        result.ManifestRecorded.ShouldBeTrue();
        result.ManifestPushed.ShouldBeTrue();
        result.BodyUpdated.ShouldBeFalse();  // body was already fresh
        result.CommentPosted.ShouldBeFalse();  // no rebase → no comment
    }

    // ════════════════════════════════════════════════════════════════════
    // 8. Rebase path
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CleanRebase_AllSidesOk_ReturnsRebased()
    {
        var (cmd, runner) = CreateCommand();
        StubHappyPath(runner);
        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        var result = Parse(output);
        result.Outcome.ShouldBe("rebased");
        result.OldHeadSha.ShouldBe(OldHeadSha);
        result.NewHeadSha.ShouldBe(NewHeadSha);
        result.BodyUpdated.ShouldBeTrue();
        result.ManifestRecorded.ShouldBeTrue();
        result.ManifestPushed.ShouldBeTrue();
        result.CommentPosted.ShouldBeTrue();

        // The manifest on disk now has a child_plan_drift rebase entry.
        var loaded = LoadManifest();
        loaded.Rebases.ShouldContain(r =>
            r.Branch == HeadBranch && r.Commit == NewHeadSha && r.Reason == "child_plan_drift"
            && r.Onto == $"refs/heads/{ParentPlanBranch}");
    }

    [Fact]
    public async Task RebaseFailed_ReturnsRebaseFailed()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseFailed(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("rebase_failed");
    }

    [Fact]
    public async Task BodyUpdateFails_ReturnsBodyUpdateFailedManifestNotRecorded()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseClean(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseSuccess(runner, HeadBranch, OldHeadSha);
        StubGhPrEditFailed(runner, PrNumber);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        var result = Parse(output);
        result.ErrorCode.ShouldBe("body_update_failed");
        result.NewHeadSha.ShouldBe(NewHeadSha);  // push succeeded
        result.BodyUpdated.ShouldBeFalse();
        result.ManifestRecorded.ShouldBeFalse();

        // Verify the manifest on disk did NOT receive a ledger entry (push not attempted).
        LoadManifest().Rebases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ManifestPushRejected_ReturnsManifestPushRejected()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseClean(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseSuccess(runner, HeadBranch, OldHeadSha);
        StubGhPrEditOk(runner, PrNumber);
        StubManifestPushRejected(runner);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("manifest_push_rejected");
    }

    [Fact]
    public async Task CommentPostFailed_ReturnsRebasedWithWarning()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseClean(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseSuccess(runner, HeadBranch, OldHeadSha);
        StubGhPrEditOk(runner, PrNumber);
        StubManifestPushOk(runner);
        StubGhPrCommentFailed(runner, PrNumber);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        var result = Parse(output);
        result.Outcome.ShouldBe("rebased");
        result.CommentPosted.ShouldBeFalse();
        result.Warnings.ShouldNotBeEmpty();
    }

    // ════════════════════════════════════════════════════════════════════
    // 9. Front-matter handling
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MalformedFrontMatter_ReturnsMalformedFrontMatter()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        // Front-matter exists but YAML inside is broken
        var body = "---\nrequests_parent_change: not-a-bool: oops\n  - bad indent\n---\n\nbody";
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseClean(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseSuccess(runner, HeadBranch, OldHeadSha);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).ErrorCode.ShouldBe("malformed_front_matter");
    }

    [Fact]
    public async Task RequestsParentChangeTrue_PreservedAfterRewrite()
    {
        var (cmd, runner) = CreateCommand();
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        // Body has requests_parent_change: true + stale snapshot
        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 }, requestsParentChange: true);
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, OldHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, OldHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", false);
        StubMergeBase(runner, OldHeadSha, $"origin/{ParentPlanBranch}", "f00ba12c");
        StubRebaseClean(runner, $"origin/{ParentPlanBranch}", "f00ba12c", OldHeadSha, NewHeadSha);
        StubPushHeadWithLeaseSuccess(runner, HeadBranch, OldHeadSha);
        // Default gh pr edit handler — body is sent via stdin (--body-file -).
        runner.When(
            (exe, args) => exe == "gh" && args.Count >= 3 && args[0] == "pr" && args[1] == "edit" && args[2] == PrNumber.ToString(),
            new ProcessResult(0, "", ""));
        StubManifestPushOk(runner);
        StubGhPrCommentOk(runner, PrNumber);

        var (_, _) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));

        // The new body comes through stdin (--body-file -). Pull it out of the
        // recorded invocation.
        var ghEdit = runner.Invocations.SingleOrDefault(i =>
            i.Executable == "gh" && i.Arguments.Count >= 3
            && i.Arguments[0] == "pr" && i.Arguments[1] == "edit" && i.Arguments[2] == PrNumber.ToString());
        ghEdit.ShouldNotBeNull();
        ghEdit!.Arguments.ShouldContain("--body-file");
        var sentBody = ghEdit.Stdin;
        sentBody.ShouldNotBeNull();
        sentBody!.ShouldContain("requests_parent_change: true");
        sentBody.ShouldContain("root: 2");
        sentBody.ShouldContain("\"200\": 1");
    }

    // ════════════════════════════════════════════════════════════════════
    // 10. Idempotent replay
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReplayAfterRebased_ReturnsNoop()
    {
        var (cmd, runner) = CreateCommand();
        // Seed a manifest as if a previous rebase committed: ledger has the entry,
        // and both branch + body are fresh.
        SeedManifest(rebases: new List<RebaseRecord>
        {
            new() { Branch = HeadBranch, Onto = $"refs/heads/{ParentPlanBranch}", Reason = "child_plan_drift", Commit = NewHeadSha, RecordedAt = DateTime.UtcNow }
        });
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        // PR head is now at NewHeadSha (rebase already pushed); body is fresh.
        var body = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 2, ["200"] = 1 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, NewHeadSha, body);
        StubRevParseOriginHead(runner, HeadBranch, NewHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", true);

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        Parse(output).Outcome.ShouldBe("noop");
    }

    [Fact]
    public async Task ReplayAfterBodyUpdateFailed_CompletesBodyAndManifest()
    {
        var (cmd, runner) = CreateCommand();
        // First-attempt artefacts: branch was pushed (so origin/HEAD == NewSha,
        // IsAncestor=true), body is still STALE (edit failed last time), no
        // ledger entry. Replay should: skip rebase, do body edit + manifest push.
        SeedManifest();
        StubEnvironmentDefaults(runner);
        StubStatusClean(runner);
        StubFetch(runner, FeatureBranch);
        StubFetch(runner, ParentPlanBranch);
        StubFetch(runner, HeadBranch);
        StubShowManifest(runner);

        var staleBody = MakeBodyWithSnapshot(new Dictionary<string, int> { ["root"] = 1, ["200"] = 0 });
        StubPrPoll(runner, PrNumber, "OPEN", HeadBranch, ParentPlanBranch, NewHeadSha, staleBody);
        StubRevParseOriginHead(runner, HeadBranch, NewHeadSha);
        StubCascadeParentNoPr(runner);
        StubIsAncestor(runner, $"origin/{ParentPlanBranch}", $"origin/{HeadBranch}", true);  // branch fresh
        StubGhPrEditOk(runner, PrNumber);
        StubManifestPushOk(runner);
        // No comment expected — we didn't run a rebase this time.

        var (_, output) = await CaptureConsoleAsync(() =>
            cmd.RebaseStaleDescendant(RootId, ItemId, ParentId, PrNumber, ancestorIds: "200,root", manifestPath: _manifestPath));
        var result = Parse(output);
        result.Outcome.ShouldBe("rebased");
        result.BodyUpdated.ShouldBeTrue();
        result.ManifestRecorded.ShouldBeTrue();
        result.ManifestPushed.ShouldBeTrue();
        result.CommentPosted.ShouldBeFalse();
    }
}
