using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Coverage for <c>polyphony worktree init-apex --apex N</c>:
/// argument validation, common-dir failure, the create-or-attach matrix
/// (with path-exists winning over branch-state), remote-branch refusal,
/// race-tolerant idempotent recovery, and JSON contract.
///
/// Uses real <see cref="GitClient"/> on top of <see cref="FakeProcessRunner"/>
/// so each git invocation is asserted at the wire level. Filesystem
/// state (apex_root, worktree_path) lives in a per-test temp directory.
/// </summary>
public sealed class WorktreeCommandsInitApexTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly string _commonDir;
    private readonly string _runsRoot;
    private readonly string _mainPath;

    public WorktreeCommandsInitApexTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "polyphony-init-apex-" + Guid.NewGuid().ToString("N")[..12]);
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

    private (WorktreeCommands cmd, FakeProcessRunner runner, string apexRoot, string worktreePath)
        Setup(int apex = 3085, bool stubCommonDir = true)
    {
        var apexRoot = Path.Combine(_runsRoot, $"apex-{apex}");
        var worktreePath = Path.Combine(apexRoot, $"feature-{apex}");

        var runner = new FakeProcessRunner();
        if (stubCommonDir)
        {
            runner.WhenExact(
                "git",
                ["rev-parse", "--path-format=absolute", "--git-common-dir"],
                new ProcessResult(0, _commonDir + "\n", ""));
        }

        return (new WorktreeCommands(new GitClient(runner)), runner, apexRoot, worktreePath);
    }

    private static WorktreeInitApexResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeInitApexResult)!;

    private static string PorcelainEntry(string path, string branch) =>
        $"worktree {path}\nHEAD 0000000000000000000000000000000000000000\nbranch refs/heads/{branch}\n\n";

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task InitApex_MissingApex_EmitsRequiredInputEnvelope()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex());

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("worktree init-apex");
        envelope.MissingArgs.ShouldContain("--apex");
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task InitApex_ZeroApex_EmitsInvalidApexFailure()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 0));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("invalid_apex");
        result.ApexId.ShouldBe(0);
        result.Branch.ShouldBeNull();
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task InitApex_NegativeApex_EmitsInvalidApexFailure()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: -1));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("invalid_apex");
        result.ApexId.ShouldBe(-1);
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Common-dir resolution ───────────────────────────────────────────

    [Fact]
    public async Task InitApex_CommonDirEmpty_EmitsCommonDirUnavailable()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("common_dir_unavailable");
        result.ApexRoot.ShouldBeNull();
        result.WorktreePath.ShouldBeNull();
        result.Branch.ShouldBeNull();
    }

    [Fact]
    public async Task InitApex_CommonDirGitFailure_EmitsCommonDirUnavailable()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(128, "", "fatal: not a git repository\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("common_dir_unavailable");
    }

    // ─── Path-exists branch (wins over branch-state) ────────────────────

    [Fact]
    public async Task InitApex_TargetIsWorktreeOnExpectedBranch_Idempotent()
    {
        var (cmd, runner, apexRoot, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath); // simulate worktree on disk
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("idempotent");
        result.Reason.ShouldBeNull();
        result.Branch.ShouldBe("feature/3085");
        result.ApexRoot.ShouldNotBeNull();
        Directory.Exists(apexRoot).ShouldBeTrue();
    }

    [Fact]
    public async Task InitApex_TargetIsWorktreeOnDifferentBranch_PathExistsWrongBranch()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/9999"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_wrong_branch");
        result.Error!.ShouldContain("feature/9999");
    }

    [Fact]
    public async Task InitApex_PathExistsAsDirectoryNotInWorktreeList_PathExistsNotWorktree()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath); // exists but not registered
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_not_worktree");
    }

    [Fact]
    public async Task InitApex_PathExistsAsFile_PathExistsNotWorktree()
    {
        var (cmd, runner, apexRoot, worktreePath) = Setup();
        Directory.CreateDirectory(apexRoot);
        File.WriteAllText(worktreePath, "stray file"); // collides with worktree path
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_not_worktree");
    }

    // ─── Branch-state branch ─────────────────────────────────────────────

    [Fact]
    public async Task InitApex_BranchMissingNoRemote_CreatedFromMain()
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

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("created");
        result.Reason.ShouldBeNull();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task InitApex_BranchMissingButRemoteExists_RemoteBranchExistsRefusal()
    {
        var (cmd, runner, _, _) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(128, "", "fatal: bad revision\n"));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/main\n  origin/feature/3085\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

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
    public async Task InitApex_BranchExistsCheckedOutElsewhere_BranchInUse()
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

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("branch_in_use");
        result.Error!.ShouldContain(holderPath);
    }

    [Fact]
    public async Task InitApex_BranchExistsNotCheckedOut_Attached()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/3085"],
            new ProcessResult(0, "deadbeef\n", ""));
        runner.WhenExact("git",
            ["worktree", "add", worktreePath, "feature/3085"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("attached");
        result.Reason.ShouldBeNull();
    }

    // ─── Race tolerance via post-failure re-list ─────────────────────────

    [Fact]
    public async Task InitApex_CreateRaces_ReListShowsExpected_Idempotent()
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

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("idempotent");
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task InitApex_CreateFails_NoRace_GitFailureWithStderr()
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

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("invalid reference");
    }

    [Fact]
    public async Task InitApex_AttachFails_NoRace_GitFailureWithStderr()
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

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("permission denied");
    }

    // ─── List failure / parse failure ────────────────────────────────────

    [Fact]
    public async Task InitApex_WorktreeListGitFails_GitFailure()
    {
        var (cmd, runner, _, _) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(128, "", "fatal: corrupt index\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("corrupt index");
    }

    [Fact]
    public async Task InitApex_WorktreeListMalformedPorcelain_GitFailure()
    {
        var (cmd, runner, _, _) = Setup();
        // Porcelain block missing leading `worktree` line — ParsePorcelain throws FormatException.
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "HEAD deadbeef\nbranch refs/heads/main\n\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("git_failure");
        result.Error!.ShouldContain("Could not parse");
    }

    // ─── Filesystem failure: apex_root is a file ─────────────────────────

    [Fact]
    public async Task InitApex_ApexRootIsAFile_FilesystemFailure()
    {
        var (cmd, runner, apexRoot, _) = Setup();
        Directory.CreateDirectory(_runsRoot);
        File.WriteAllText(apexRoot, "stray file"); // CreateDirectory will throw

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("filesystem_failure");
        result.Error!.ShouldContain(apexRoot);
        // No worktree-list call should have happened — we failed before the matrix.
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "list");
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task InitApex_SnakeCaseFieldNames_PresentInRawJson()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        output.ShouldContain("\"apex_id\"");
        output.ShouldContain("\"apex_root\"");
        output.ShouldContain("\"worktree_path\"");
        output.ShouldContain("\"branch\"");
        output.ShouldContain("\"outcome\"");
        // null fields omitted on success
        output.ShouldNotContain("\"reason\"");
        output.ShouldNotContain("\"error\"");
        // PascalCase forms must NOT appear
        output.ShouldNotContain("ApexId");
        output.ShouldNotContain("WorktreePath");
    }

    [Fact]
    public async Task InitApex_NullPathFields_OmittedOnPreResolutionFailure()
    {
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

        exit.ShouldBe(ExitCodes.Success);
        output.ShouldNotContain("\"apex_root\"");
        output.ShouldNotContain("\"worktree_path\"");
        output.ShouldNotContain("\"branch\"");
        output.ShouldContain("\"reason\":\"common_dir_unavailable\"");
    }

    // ─── Resolved-paths surfacing (PR 3 launcher dependency) ────────────

    [Fact]
    public async Task InitApex_SuccessfulOutcome_PopulatesRunsRootAndMainWorktreePath()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

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
    public async Task InitApex_FailureAfterPathResolution_PopulatesRunsRootAndMainWorktreePath()
    {
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/9999"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085));

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
    public async Task InitApex_DryRun_NewBranchPath_NoMutations_EmitsDryRunOutcome()
    {
        var (cmd, runner, apexRoot, worktreePath) = Setup();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(_mainPath, "main"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("dry_run");
        result.DryRun.ShouldBeTrue();
        result.Reason.ShouldBeNull();
        result.WorktreePath.ShouldBe(worktreePath);
        result.RunsRoot.ShouldBe(_runsRoot);
        result.MainWorktreePath.ShouldBe(_mainPath);
        // Mutating side effects must NOT have happened.
        Directory.Exists(apexRoot).ShouldBeFalse();
        Directory.Exists(worktreePath).ShouldBeFalse();
        // No worktree add / rev-parse for the apex branch should have run.
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "add");
    }

    [Fact]
    public async Task InitApex_DryRun_AlreadyOnExpectedBranch_StillEmitsDryRunOutcome()
    {
        // Even when the matrix would have classified as 'idempotent',
        // dry-run reports 'dry_run'. Operator can infer "no work needed"
        // from the absence of a needs-create indicator.
        var (cmd, runner, apexRoot, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("dry_run");
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task InitApex_DryRun_PathExistsWrongBranch_PropagatesFailure()
    {
        // Hard-refusal cases must surface at dry-run time so the operator
        // sees the problem before -Commit (the launcher's contract).
        var (cmd, runner, _, worktreePath) = Setup();
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(worktreePath, "feature/9999"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_wrong_branch");
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task InitApex_DryRun_BranchInUse_PropagatesFailure()
    {
        var (cmd, runner, _, _) = Setup();
        var stalePath = Path.Combine(_tempDir, "polyphony-old", "feature-3085");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(stalePath, "feature/3085"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("branch_in_use");
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task InitApex_DryRun_PathExistsNotWorktree_PropagatesFailure()
    {
        var (cmd, runner, apexRoot, worktreePath) = Setup();
        Directory.CreateDirectory(apexRoot);
        Directory.CreateDirectory(worktreePath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("path_exists_not_worktree");
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task InitApex_DryRun_PreResolutionFailure_DryRunFlagPreserved()
    {
        // Even on common_dir failure, dry_run flag must round-trip so
        // the launcher can distinguish "dry-run that hit an error" from
        // "live attempt that hit an error".
        var (cmd, runner, _, _) = Setup(stubCommonDir: false);
        runner.WhenExact(
            "git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.InitApex(apex: 3085, dryRun: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Outcome.ShouldBe("failed");
        result.Reason.ShouldBe("common_dir_unavailable");
        result.DryRun.ShouldBeTrue();
    }
}
