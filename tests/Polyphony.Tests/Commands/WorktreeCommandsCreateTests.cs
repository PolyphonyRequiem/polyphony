using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Coverage for <c>polyphony worktree create --apex N --branch B [--ref R]</c>:
/// argument validation, branch-grammar validation, the bootstrap dependency
/// (apex feature worktree must already exist on <c>feature/{N}</c>),
/// the create-or-attach matrix (path-exists wins over branch-state),
/// remote-branch refusal (workflow-rerun safety), race-tolerant
/// idempotent recovery, and JSON contract.
///
/// Uses real <see cref="GitClient"/> on top of <see cref="FakeProcessRunner"/>
/// so each git invocation is asserted at the wire level. Filesystem
/// state lives in a per-test temp directory.
/// </summary>
public sealed class WorktreeCommandsCreateTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly string _commonDir;
    private readonly string _runsRoot;
    private readonly string _mainPath;

    public WorktreeCommandsCreateTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "polyphony-create-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_tempDir);
        _commonDir = Path.Combine(_tempDir, "polyphony.git");
        Directory.CreateDirectory(_commonDir);
        _mainPath = Path.Combine(_tempDir, "polyphony");
        _runsRoot = Path.Combine(_tempDir, "polyphony-runs");
    }

    public override void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
        base.Dispose();
    }

    private (WorktreeCommands cmd, FakeProcessRunner runner, string apexRoot, string featurePath, string targetPath)
        Setup(int apex = 3085, string slug = "impl-3085-3072", bool stubCommonDir = true)
    {
        var apexRoot = Path.Combine(_runsRoot, $"apex-{apex}");
        var featurePath = Path.Combine(apexRoot, $"feature-{apex}");
        var targetPath = Path.Combine(apexRoot, slug);

        var runner = new FakeProcessRunner();
        if (stubCommonDir)
        {
            runner.WhenExact(
                "git",
                ["rev-parse", "--path-format=absolute", "--git-common-dir"],
                new ProcessResult(0, _commonDir + "\n", ""));
        }

        return (new WorktreeCommands(new GitClient(runner)), runner, apexRoot, featurePath, targetPath);
    }

    private static WorktreeCreateResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeCreateResult)!;

    private static string PorcelainEntry(string path, string branch) =>
        $"worktree {path}\nHEAD 0000000000000000000000000000000000000000\nbranch refs/heads/{branch}\n\n";

    /// <summary>Porcelain block for an apex whose feature worktree IS initialized.</summary>
    private string Bootstrapped(string featurePath, int apex, params (string path, string branch)[] extra)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(PorcelainEntry(_mainPath, "main"));
        sb.Append(PorcelainEntry(featurePath, $"feature/{apex}"));
        foreach (var (p, b) in extra) sb.Append(PorcelainEntry(p, b));
        return sb.ToString();
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task Create_MissingApex_EmitsRequiredInputEnvelope()
    {
        var (cmd, runner, _, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Create(branch: "impl/3085-3072"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.MissingArgs.ShouldContain("--apex");
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_MissingBranch_EmitsRequiredInputEnvelope()
    {
        var (cmd, runner, _, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Create(apex: 3085));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.MissingArgs.ShouldContain("--branch");
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_ZeroApex_EmitsInvalidApex()
    {
        var (cmd, runner, _, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 0, branch: "impl/0-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("invalid_apex");
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Branch grammar ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_InvalidBranchGrammar_EmitsInvalidBranch()
    {
        var (cmd, runner, _, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "garbage"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("invalid_branch");
        result.Branch.ShouldBeNull();
        result.Slug.ShouldBeNull();
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_BranchApexMismatch_EmitsBranchApexMismatch()
    {
        var (cmd, runner, _, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/9999-1234"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("branch_apex_mismatch");
        result.Branch.ShouldBe("impl/9999-1234");
        result.Slug.ShouldBe("impl-9999-1234");
        result.Error!.ShouldContain("9999");
        result.Error!.ShouldContain("3085");
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_FeatureBranch_EmitsUnsupportedBranchKind()
    {
        var (cmd, runner, _, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "feature/3085", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("unsupported_branch_kind");
        result.Error!.ShouldContain("init-apex");
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Common-dir resolution ───────────────────────────────────────────

    [Fact]
    public async Task Create_CommonDirEmpty_EmitsCommonDirUnavailable()
    {
        var (cmd, runner, _, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("common_dir_unavailable");
        result.ApexRoot.ShouldBeNull();
        result.WorktreePath.ShouldBeNull();
    }

    // ─── apex_not_initialized ────────────────────────────────────────────

    [Fact]
    public async Task Create_ApexFeatureMissing_EmitsApexNotInitialized()
    {
        var (cmd, runner, _, _, _) = Setup();
        // List shows main only — apex feature worktree not registered.
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("apex_not_initialized");
        result.Error!.ShouldContain("init-apex");
        // Critically, no rev-parse, no branch -r, no add — bail before any branch-state work.
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 1 && i.Arguments[0] == "rev-parse"
            && i.Arguments.Any(a => a.StartsWith("refs/heads/", StringComparison.Ordinal)));
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "add");
    }

    [Fact]
    public async Task Create_ApexFeatureRegisteredOnWrongBranch_EmitsApexNotInitialized()
    {
        var (cmd, runner, _, featurePath, _) = Setup();
        // feature path exists in worktree list but on the wrong branch — partial init.
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0,
                PorcelainEntry(_mainPath, "main") +
                PorcelainEntry(featurePath, "feature/9999"),
                ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("apex_not_initialized");
    }

    // ─── Path-exists matrix (after bootstrap check passes) ───────────────

    [Fact]
    public async Task Create_TargetIsWorktreeOnExpectedBranch_Idempotent()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        Directory.CreateDirectory(targetPath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085,
                (targetPath, "impl/3085-3072")), ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("idempotent");
        result.Reason.ShouldBeNull();
        result.Branch.ShouldBe("impl/3085-3072");
    }

    [Fact]
    public async Task Create_TargetIsWorktreeOnDifferentBranch_PathExistsWrongBranch()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        Directory.CreateDirectory(targetPath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085,
                (targetPath, "impl/3085-9999")), ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_wrong_branch");
        result.Error!.ShouldContain("impl/3085-9999");
    }

    [Fact]
    public async Task Create_PathExistsAsDirectoryNotInWorktreeList_PathExistsNotWorktree()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        Directory.CreateDirectory(targetPath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_not_worktree");
    }

    [Fact]
    public async Task Create_PathExistsAsFile_PathExistsNotWorktree()
    {
        var (cmd, runner, apexRoot, featurePath, targetPath) = Setup();
        Directory.CreateDirectory(apexRoot);
        File.WriteAllText(targetPath, "stray file");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_not_worktree");
    }

    // ─── Branch-state matrix ─────────────────────────────────────────────

    [Fact]
    public async Task Create_BranchMissingNoRemoteRefSupplied_CreatedFromRef()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n  origin/feature/3085\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", "-b", "impl/3085-3072", targetPath, "main"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("created");
        result.Reason.ShouldBeNull();
        result.Ref.ShouldBe("main");
    }

    [Fact]
    public async Task Create_BranchMissingNoRemoteNoRef_EmitsRefRequired()
    {
        var (cmd, runner, _, featurePath, _) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("ref_required");
        result.Ref.ShouldBeNull();
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "add");
    }

    [Fact]
    public async Task Create_BranchMissingButRemoteExists_RefusesEvenWhenRefSupplied()
    {
        var (cmd, runner, _, featurePath, _) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n  origin/impl/3085-3072\n", ""));

        // Operator passes --ref, but we still refuse: workflow-rerun safety.
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("remote_branch_exists");
        result.Error!.ShouldContain("origin/impl/3085-3072");
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "add");
    }

    [Fact]
    public async Task Create_BranchExistsCheckedOutElsewhere_BranchInUse()
    {
        var (cmd, runner, _, featurePath, _) = Setup();
        var holderPath = Path.Combine(_tempDir, "elsewhere", "impl-3085-3072");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085,
                (holderPath, "impl/3085-3072")), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(0, "deadbeef\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("branch_in_use");
        result.Error!.ShouldContain(holderPath);
    }

    [Fact]
    public async Task Create_BranchExistsNotCheckedOut_AttachedEvenWithoutRef()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(0, "deadbeef\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", targetPath, "impl/3085-3072"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        // No --ref; should still attach.
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("attached");
        result.Reason.ShouldBeNull();
    }

    // ─── Race tolerance ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_AddRaces_ReListShowsExpected_Idempotent()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        runner.WhenStartsWithSequence("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""),
            new ProcessResult(0, Bootstrapped(featurePath, 3085,
                (targetPath, "impl/3085-3072")), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", "-b", "impl/3085-3072", targetPath, "main"],
            new ProcessResult(128, "", "fatal: '" + targetPath + "' already exists\n"));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("idempotent");
    }

    [Fact]
    public async Task Create_AddFails_NoRace_GitFailureWithStderr()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        runner.WhenStartsWithSequence("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""),
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", "-b", "impl/3085-3072", targetPath, "garbage-ref"],
            new ProcessResult(128, "", "fatal: invalid reference: garbage-ref\n"));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "garbage-ref"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("invalid reference");
    }

    [Fact]
    public async Task Create_AttachRaces_ReListShowsExpected_Idempotent()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        runner.WhenStartsWithSequence("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""),
            new ProcessResult(0, Bootstrapped(featurePath, 3085,
                (targetPath, "impl/3085-3072")), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(0, "deadbeef\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", targetPath, "impl/3085-3072"],
            new ProcessResult(128, "", "fatal: branch already in use\n"));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("idempotent");
    }

    [Fact]
    public async Task Create_AttachFails_NoRace_GitFailureWithStderr()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        runner.WhenStartsWithSequence("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""),
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(0, "deadbeef\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", targetPath, "impl/3085-3072"],
            new ProcessResult(128, "", "fatal: cannot create worktree: permission denied\n"));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("permission denied");
    }

    // ─── List failure ────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WorktreeListGitFails_GitFailure()
    {
        var (cmd, runner, _, _, _) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(128, "", "fatal: corrupt index\n"));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("corrupt index");
    }

    // ─── Different branch kinds ──────────────────────────────────────────

    [Fact]
    public async Task Create_PlanDescendantBranch_ResolvesSlugCorrectly()
    {
        // plan/3085-9999 → slug plan-3085-9999
        var (cmd, runner, _, featurePath, targetPath) = Setup(slug: "plan-3085-9999");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/plan/3085-9999"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", "-b", "plan/3085-9999", targetPath, "main"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "plan/3085-9999", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("created");
        result.Slug.ShouldBe("plan-3085-9999");
        result.WorktreePath.ShouldBe(targetPath);
    }

    [Fact]
    public async Task Create_MgBranch_ResolvesSlugCorrectly()
    {
        // mg/3085_pg-foo → slug mg-3085_pg-foo (underscores preserved)
        var (cmd, runner, _, featurePath, targetPath) = Setup(slug: "mg-3085_pg-foo");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/mg/3085_pg-foo"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", "-b", "mg/3085_pg-foo", targetPath, "main"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "mg/3085_pg-foo", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("created");
        result.Slug.ShouldBe("mg-3085_pg-foo");
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task Create_SnakeCaseFieldNames_PresentInRawJson()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        Directory.CreateDirectory(targetPath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085,
                (targetPath, "impl/3085-3072")), ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072", @ref: "main"));

        exit.ShouldBe(ExitCodes.Success);
        output.ShouldContain("\"apex_id\"");
        output.ShouldContain("\"apex_root\"");
        output.ShouldContain("\"worktree_path\"");
        output.ShouldContain("\"branch\"");
        output.ShouldContain("\"slug\"");
        output.ShouldContain("\"ref\"");
        output.ShouldContain("\"outcome\"");
        // null fields omitted on success
        output.ShouldNotContain("\"reason\"");
        output.ShouldNotContain("\"error\"");
        // PascalCase forms must NOT appear
        output.ShouldNotContain("ApexId");
        output.ShouldNotContain("WorktreePath");
    }

    [Fact]
    public async Task Create_NullRef_OmittedWhenNotSupplied()
    {
        var (cmd, runner, _, featurePath, targetPath) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, Bootstrapped(featurePath, 3085), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/impl/3085-3072"],
            new ProcessResult(0, "deadbeef\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", targetPath, "impl/3085-3072"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Create(apex: 3085, branch: "impl/3085-3072"));

        exit.ShouldBe(ExitCodes.Success);
        output.ShouldNotContain("\"ref\"");
        output.ShouldContain("\"outcome\":\"attached\"");
    }
}
