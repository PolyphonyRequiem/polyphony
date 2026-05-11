using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class WorktreeCommandsAssertCleanTests : CommandTestBase
{
    private static (WorktreeCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        return (new WorktreeCommands(new GitClient(runner)), runner);
    }

    private static WorktreeAssertCleanResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeAssertCleanResult)!;

    private static string CreateTempWorktree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyphony-wt-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Register the canned "git rev-parse --git-dir" response so the
    /// in-progress probe finds no sentinel files (returns null operation).
    /// Pointed at a non-existent gitdir under the same temp root so each
    /// File.Exists / Directory.Exists check returns false.
    /// </summary>
    private static string SeedNotInProgress(FakeProcessRunner runner, string path)
    {
        var fakeGitDir = Path.Combine(path, ".git-no-such");
        runner.WhenExact("git",
            ["-C", path, "rev-parse", "--path-format=absolute", "--git-dir"],
            new ProcessResult(0, fakeGitDir + "\n", ""));
        return fakeGitDir;
    }

    private static void SeedStatus(FakeProcessRunner runner, string path, string stdout)
        => runner.WhenExact("git",
            ["-C", path, "--no-optional-locks", "status", "--porcelain"],
            new ProcessResult(0, stdout, ""));

    private static void SeedBranch(FakeProcessRunner runner, string path, string branch)
        => runner.WhenExact("git",
            ["-C", path, "branch", "--show-current"],
            new ProcessResult(0, branch + "\n", ""));

    // ─── Path validation ──────────────────────────────────────────────────

    [Fact]
    public async Task AssertClean_PathMissing_ReasonPathMissingAndExitsZero()
    {
        var (cmd, runner) = CreateCommand();
        var missing = Path.Combine(Path.GetTempPath(), $"polyphony-wt-missing-{Guid.NewGuid():N}");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertClean(path: missing, expectedBranch: "main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Ok.ShouldBeFalse();
        result.Reason.ShouldBe("path_missing");
        result.ExpectedBranch.ShouldBe("main");
        result.CurrentBranch.ShouldBeNull();
        result.DirtyPaths.ShouldBeEmpty();
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Reason routing: stderr discrimination ─────────────────────────

    [Fact]
    public async Task AssertClean_NotARepoStderr_ReasonNotAWorktree()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            runner.WhenExact("git",
                ["-C", path, "--no-optional-locks", "status", "--porcelain"],
                new ProcessResult(128, "", "fatal: not a git repository\n"));

            var (exit, output) = await CaptureConsoleAsync(() => cmd.AssertClean(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("not_a_worktree");
            result.Error!.ShouldContain("not a git repository");
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AssertClean_DubiousOwnership_ReasonGitFailedNotNotAWorktree()
    {
        // git refuses with "fatal: detected dubious ownership in repository"
        // when safe.directory blocks the path. The remediation is configuration,
        // not "create a worktree" — surface as git_failed so launcher prompts
        // for the right fix.
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            runner.WhenExact("git",
                ["-C", path, "--no-optional-locks", "status", "--porcelain"],
                new ProcessResult(128, "",
                    "fatal: detected dubious ownership in repository at '" + path + "'\n"));

            var (exit, output) = await CaptureConsoleAsync(() => cmd.AssertClean(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("git_failed");
            result.Error!.ShouldContain("dubious ownership");
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AssertClean_LockedIndex_ReasonGitFailed()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            runner.WhenExact("git",
                ["-C", path, "--no-optional-locks", "status", "--porcelain"],
                new ProcessResult(128, "",
                    "fatal: Unable to create '.git/index.lock': File exists.\n"));

            var (exit, output) = await CaptureConsoleAsync(() => cmd.AssertClean(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("git_failed");
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    // ─── Reason routing: in-progress operations ────────────────────────

    [Fact]
    public async Task AssertClean_PausedRebase_ReasonGitOperationInProgress()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        var gitDir = Path.Combine(path, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge"));
        try
        {
            SeedStatus(runner, path, "");
            SeedBranch(runner, path, "feature/3085");
            // Real gitdir → real sentinel directory → in-progress probe fires.
            runner.WhenExact("git",
                ["-C", path, "rev-parse", "--path-format=absolute", "--git-dir"],
                new ProcessResult(0, gitDir + "\n", ""));

            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.AssertClean(path: path, expectedBranch: "feature/3085"));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("git_operation_in_progress");
            result.InProgressOperation.ShouldBe("rebase-merge");
            // Even on the expected branch with clean porcelain — in-progress wins.
            result.CurrentBranch.ShouldBe("feature/3085");
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AssertClean_PausedMerge_ReasonGitOperationInProgress()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        var gitDir = Path.Combine(path, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "MERGE_HEAD"), "deadbeef");
        try
        {
            SeedStatus(runner, path, "");
            SeedBranch(runner, path, "main");
            runner.WhenExact("git",
                ["-C", path, "rev-parse", "--path-format=absolute", "--git-dir"],
                new ProcessResult(0, gitDir + "\n", ""));

            var (exit, output) = await CaptureConsoleAsync(() => cmd.AssertClean(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("git_operation_in_progress");
            result.InProgressOperation.ShouldBe("merge");
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    // ─── Reason routing: dirty / wrong-branch ──────────────────────────

    [Fact]
    public async Task AssertClean_DirtyWorktree_ReasonDirtyAndCarriesPaths()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            SeedStatus(runner, path, " M src/foo.cs\n?? scratch.txt\n");
            SeedBranch(runner, path, "main");
            SeedNotInProgress(runner, path);

            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.AssertClean(path: path, expectedBranch: "main"));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("dirty");
            result.CurrentBranch.ShouldBe("main");
            result.DirtyPaths.Count.ShouldBe(2);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AssertClean_WrongBranch_ReasonWrongBranch()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            SeedStatus(runner, path, "");
            SeedBranch(runner, path, "feature/other");
            SeedNotInProgress(runner, path);

            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.AssertClean(path: path, expectedBranch: "feature/3085"));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("wrong_branch");
            result.CurrentBranch.ShouldBe("feature/other");
            result.ExpectedBranch.ShouldBe("feature/3085");
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AssertClean_DetachedHeadWithExpectedBranch_ReasonWrongBranch()
    {
        // Detached HEAD reports current_branch=null; with an expected
        // branch supplied, that's a wrong-branch failure (null ≠ "main").
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            SeedStatus(runner, path, "");
            SeedBranch(runner, path, "");
            SeedNotInProgress(runner, path);

            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.AssertClean(path: path, expectedBranch: "main"));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("wrong_branch");
            result.CurrentBranch.ShouldBeNull();
            result.ExpectedBranch.ShouldBe("main");
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AssertClean_DirtyOnWrongBranch_DirtyTakesPrecedence()
    {
        // Operator needs the dirty-paths diagnostic before the branch
        // mismatch — they must clean up before they can switch branches.
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            SeedStatus(runner, path, " M src/foo.cs\n");
            SeedBranch(runner, path, "feature/other");
            SeedNotInProgress(runner, path);

            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.AssertClean(path: path, expectedBranch: "feature/3085"));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeFalse();
            result.Reason.ShouldBe("dirty");
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    // ─── Happy paths ──────────────────────────────────────────────────────

    [Fact]
    public async Task AssertClean_CleanNoExpectedBranch_OkAndExpectedBranchNull()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            SeedStatus(runner, path, "");
            SeedBranch(runner, path, "main");
            SeedNotInProgress(runner, path);

            var (exit, output) = await CaptureConsoleAsync(() => cmd.AssertClean(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeTrue();
            result.Reason.ShouldBeNull();
            result.CurrentBranch.ShouldBe("main");
            result.ExpectedBranch.ShouldBeNull();
            result.DirtyPaths.ShouldBeEmpty();
            result.Error.ShouldBeNull();
            result.InProgressOperation.ShouldBeNull();
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AssertClean_CleanOnExpectedBranch_Ok()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            SeedStatus(runner, path, "");
            SeedBranch(runner, path, "feature/3085");
            SeedNotInProgress(runner, path);

            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.AssertClean(path: path, expectedBranch: "feature/3085"));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Ok.ShouldBeTrue();
            result.Reason.ShouldBeNull();
            result.CurrentBranch.ShouldBe("feature/3085");
            result.ExpectedBranch.ShouldBe("feature/3085");
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
