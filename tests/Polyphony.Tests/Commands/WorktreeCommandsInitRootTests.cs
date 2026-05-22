using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Coverage for <c>polyphony worktree init-root --root N</c>:
/// argument validation, common-dir failure, the create-or-attach matrix
/// (with path-exists winning over branch-state), remote-branch refusal,
/// race-tolerant idempotent recovery, and JSON contract.
///
/// Uses real <see cref="GitClient"/> on top of <see cref="FakeProcessRunner"/>
/// so each git invocation is asserted at the wire level. Filesystem
/// state (root_root, worktree_path) lives in a per-test temp directory.
/// </summary>
public sealed class WorktreeCommandsInitRootTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly string _commonDir;
    private readonly string _runsRoot;
    private readonly string _mainPath;

    public WorktreeCommandsInitRootTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "polyphony-init-root-" + Guid.NewGuid().ToString("N")[..12]);
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
            // best-effort cleanup; per-test temp dirs leak but tests pass
        }
        base.Dispose();
    }

    private (WorktreeCommands cmd, FakeProcessRunner runner, string rootRoot, string worktreePath)
        Setup(int root = 3085, bool stubCommonDir = true)
    {
        var rootRoot = Path.Combine(_runsRoot, $"root-{root}");
        var worktreePath = Path.Combine(rootRoot, $"feature-{root}");

        var runner = new FakeProcessRunner();
        if (stubCommonDir)
        {
            runner.WhenExact(
                "git",
                ["rev-parse", "--path-format=absolute", "--git-common-dir"],
                new ProcessResult(0, _commonDir + "\n", ""));
        }

        return (new WorktreeCommands(new GitClient(runner)), runner, rootRoot, worktreePath);
    }

    private static WorktreeInitRootResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeInitRootResult)!;

    private static string PorcelainEntry(string path, string branch) =>
        $"worktree {path}\nHEAD 0000000000000000000000000000000000000000\nbranch refs/heads/{branch}\n\n";

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task InitRoot_MissingRoot_EmitsRequiredInputEnvelope()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot());

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("worktree init-root");
        envelope.MissingArgs.ShouldContain("--root");
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task InitRoot_ZeroRoot_EmitsInvalidRootFailure()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 0));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("invalid_root");
        result.RootId.ShouldBe(0);
        result.Branch.ShouldBeNull();
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task InitRoot_NegativeRoot_EmitsInvalidRootFailure()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: -1));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("invalid_root");
        result.RootId.ShouldBe(-1);
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Common-dir resolution ───────────────────────────────────────────

    [Fact]
    public async Task InitRoot_CommonDirEmpty_EmitsCommonDirUnavailable()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("common_dir_unavailable");
        result.RootRoot.ShouldBeNull();
        result.WorktreePath.ShouldBeNull();
        result.Branch.ShouldBeNull();
    }

    [Fact]
    public async Task InitRoot_CommonDirGitFailure_EmitsCommonDirUnavailable()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(128, "", "fatal: not a git repository\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("common_dir_unavailable");
    }

    // ─── Path-exists branch (wins over branch-state) ────────────────────

    [Fact]
    public async Task InitRoot_TargetIsWorktreeOnExpectedBranch_Idempotent()
    {
        var (cmd, runner, rootRoot, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath); // simulate worktree on disk
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("idempotent");
        result.Reason.ShouldBeNull();
        result.Branch.ShouldBe("feature/3085");
        result.RootRoot.ShouldNotBeNull();
        Directory.Exists(rootRoot).ShouldBeTrue();
    }

    [Fact]
    public async Task InitRoot_TargetIsWorktreeOnDifferentBranch_PathExistsWrongBranch()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/9999"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_wrong_branch");
        result.Error!.ShouldContain("feature/9999");
    }

    [Fact]
    public async Task InitRoot_PathExistsAsDirectoryNotInWorktreeList_PathExistsNotWorktree()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath); // exists but not registered
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_not_worktree");
    }

    [Fact]
    public async Task InitRoot_PathExistsAsFile_PathExistsNotWorktree()
    {
        var (cmd, runner, rootRoot, worktreePath) = Setup();
        Directory.CreateDirectory(rootRoot);
        File.WriteAllText(worktreePath, "stray file"); // collides with worktree path
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_not_worktree");
    }

    // ─── Branch-state branch ─────────────────────────────────────────────

    [Fact]
    public async Task InitRoot_BranchMissingNoRemote_CreatedFromMain()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", "-b", "feature/3085", worktreePath, "main"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("created");
        result.Reason.ShouldBeNull();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task InitRoot_BranchMissingButRemoteExists_RemoteBranchExistsRefusal()
    {
        var (cmd, runner, _, _) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n  origin/feature/3085\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("remote_branch_exists");
        result.Error!.ShouldContain("origin/feature/3085");
        // Critically, no `git worktree add` should have been called.
        runner.Invocations.ShouldNotContain(
            i => i.Arguments.Count >= 3 && i.Arguments[0] == "worktree" && i.Arguments[1] == "add");
    }

    [Fact]
    public async Task InitRoot_BranchExistsCheckedOutElsewhere_BranchInUse()
    {
        var (cmd, runner, _, _) = Setup();
        var holderPath = Path.Combine(_tempDir, "elsewhere", "feature-3085");
        var porcelain =
            PorcelainEntry(_mainPath, "main") +
            PorcelainEntry(holderPath, "feature/3085");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, porcelain, ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(0, "deadbeef\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("branch_in_use");
        result.Error!.ShouldContain(holderPath);
    }

    [Fact]
    public async Task InitRoot_BranchExistsNotCheckedOut_Attached()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(0, "deadbeef\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", worktreePath, "feature/3085"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("attached");
        result.Reason.ShouldBeNull();
    }

    // ─── Race tolerance via post-failure re-list ─────────────────────────

    [Fact]
    public async Task InitRoot_CreateRaces_ReListShowsExpected_Idempotent()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        // First list: nothing. Second list (probe after add fails): worktree present.
        runner.WhenStartsWithSequence("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""),
            new ProcessResult(0,
                PorcelainEntry(_mainPath, "main") + PorcelainEntry(worktreePath, "feature/3085"),
                ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", "-b", "feature/3085", worktreePath, "main"],
            new ProcessResult(128, "", "fatal: '" + worktreePath + "' already exists\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("idempotent");
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task InitRoot_CreateFails_NoRace_GitFailureWithStderr()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        runner.WhenStartsWithSequence("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""),
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), "")); // probe still empty
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", "-b", "feature/3085", worktreePath, "main"],
            new ProcessResult(128, "", "fatal: invalid reference: main\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("invalid reference");
    }

    [Fact]
    public async Task InitRoot_AttachFails_NoRace_GitFailureWithStderr()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        runner.WhenStartsWithSequence("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""),
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(0, "deadbeef\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", worktreePath, "feature/3085"],
            new ProcessResult(128, "", "fatal: cannot create worktree: permission denied\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("permission denied");
    }

    // ─── List failure / parse failure ────────────────────────────────────

    [Fact]
    public async Task InitRoot_WorktreeListGitFails_GitFailure()
    {
        var (cmd, runner, _, _) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(128, "", "fatal: corrupt index\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("corrupt index");
    }

    [Fact]
    public async Task InitRoot_WorktreeListMalformedPorcelain_GitFailure()
    {
        var (cmd, runner, _, _) = Setup();
        // Porcelain block missing leading `worktree` line — ParsePorcelain throws FormatException.
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "HEAD deadbeef\nbranch refs/heads/main\n\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("Could not parse");
    }

    // ─── Filesystem failure: root_root is a file ─────────────────────────

    [Fact]
    public async Task InitRoot_RootRootIsAFile_FilesystemFailure()
    {
        var (cmd, runner, rootRoot, _) = Setup();
        Directory.CreateDirectory(_runsRoot);
        File.WriteAllText(rootRoot, "stray file"); // CreateDirectory will throw

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("filesystem_failure");
        result.Error!.ShouldContain(rootRoot);
        // No worktree-list call should have happened — we failed before the matrix.
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "list");
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task InitRoot_SnakeCaseFieldNames_PresentInRawJson()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"root_root\"");
        output.ShouldContain("\"worktree_path\"");
        output.ShouldContain("\"branch\"");
        output.ShouldContain("\"outcome\"");
        // null fields omitted on success
        output.ShouldNotContain("\"reason\"");
        output.ShouldNotContain("\"error\"");
        // PascalCase forms must NOT appear
        output.ShouldNotContain("RootId");
        output.ShouldNotContain("WorktreePath");
    }

    [Fact]
    public async Task InitRoot_NullPathFields_OmittedOnPreResolutionFailure()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        output.ShouldNotContain("\"root_root\"");
        output.ShouldNotContain("\"worktree_path\"");
        output.ShouldNotContain("\"branch\"");
        output.ShouldContain("\"reason\":\"common_dir_unavailable\"");
    }

    // ─── Resolved-paths surfacing (PR 3 launcher dependency) ────────────

    [Fact]
    public async Task InitRoot_SuccessfulOutcome_PopulatesRunsRootAndMainWorktreePath()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("idempotent");
        result.RunsRoot.ShouldBe(_runsRoot);
        result.MainWorktreePath.ShouldBe(_mainPath);
        // Wire-format check: snake_case keys present
        output.ShouldContain("\"runs_root\":");
        output.ShouldContain("\"main_worktree_path\":");
    }

    [Fact]
    public async Task InitRoot_FailureAfterPathResolution_PopulatesRunsRootAndMainWorktreePath()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/9999"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_wrong_branch");
        // Even on failure, the launcher needs runs_root + main_worktree_path
        // so it can render the boundary-aware diagnostic.
        result.RunsRoot.ShouldBe(_runsRoot);
        result.MainWorktreePath.ShouldBe(_mainPath);
    }

    // ─── Dry-run mode (PR 3 launcher dependency) ────────────────────────

    [Fact]
    public async Task InitRoot_DryRun_NewBranchPath_NoMutations_EmitsDryRunOutcome()
    {
        var (cmd, runner, rootRoot, worktreePath) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("dry_run");
        result.DryRun.ShouldBeTrue();
        result.Reason.ShouldBeNull();
        result.WorktreePath.ShouldBe(worktreePath);
        result.RunsRoot.ShouldBe(_runsRoot);
        result.MainWorktreePath.ShouldBe(_mainPath);
        // Mutating side effects must NOT have happened.
        Directory.Exists(rootRoot).ShouldBeFalse();
        Directory.Exists(worktreePath).ShouldBeFalse();
        // No worktree add / rev-parse for the root branch should have run.
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "add");
    }

    [Fact]
    public async Task InitRoot_DryRun_AlreadyOnExpectedBranch_StillEmitsDryRunOutcome()
    {
        // Even when the matrix would have classified as 'idempotent',
        // dry-run reports 'dry_run'. Operator can infer "no work needed"
        // from the absence of a needs-create indicator.
        var (cmd, runner, rootRoot, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("dry_run");
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task InitRoot_DryRun_PathExistsWrongBranch_PropagatesFailure()
    {
        // Hard-refusal cases must surface at dry-run time so the operator
        // sees the problem before -Commit (the launcher's contract).
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/9999"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_wrong_branch");
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task InitRoot_DryRun_BranchInUse_PropagatesFailure()
    {
        var (cmd, runner, _, _) = Setup();
        var stalePath = Path.Combine(_tempDir, "polyphony-old", "feature-3085");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(stalePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("branch_in_use");
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task InitRoot_DryRun_PathExistsNotWorktree_PropagatesFailure()
    {
        var (cmd, runner, rootRoot, worktreePath) = Setup();
        Directory.CreateDirectory(rootRoot);
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_not_worktree");
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task InitRoot_DryRun_PreResolutionFailure_DryRunFlagPreserved()
    {
        // Even on common_dir failure, dry_run flag must round-trip so
        // the launcher can distinguish "dry-run that hit an error" from
        // "live attempt that hit an error".
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitRoot(root: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("common_dir_unavailable");
        result.DryRun.ShouldBeTrue();
    }
}
