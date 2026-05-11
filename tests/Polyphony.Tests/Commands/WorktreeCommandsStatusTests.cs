using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class WorktreeCommandsStatusTests : CommandTestBase
{
    private static (WorktreeCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        return (new WorktreeCommands(new GitClient(runner)), runner);
    }

    private static WorktreeStatusResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeStatusResult)!;

    /// <summary>
    /// Allocate a real temp directory for tests that exercise the
    /// <c>Directory.Exists</c> branch. Returned path is the normalized
    /// absolute form (matching what <c>Path.GetFullPath</c> would emit).
    /// </summary>
    private static string CreateTempWorktree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyphony-wt-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    // ─── Path validation ──────────────────────────────────────────────────

    [Fact]
    public async Task Status_PathDoesNotExist_EmitsErrorAndExitsZero()
    {
        var (cmd, runner) = CreateCommand();
        var missing = Path.Combine(Path.GetTempPath(), $"polyphony-wt-missing-{Guid.NewGuid():N}");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Status(path: missing));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.IsClean.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("does not exist");
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Happy paths ──────────────────────────────────────────────────────

    [Fact]
    public async Task Status_CleanWorktree_ReportsCleanAndCurrentBranch()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            runner.WhenExact("git", ["-C", path, "--no-optional-locks", "status", "--porcelain"], new ProcessResult(0, "", ""));
            runner.WhenExact("git", ["-C", path, "branch", "--show-current"], new ProcessResult(0, "main\n", ""));

            var (exit, output) = await CaptureConsoleAsync(() => cmd.Status(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.Path.ShouldBe(path);
            result.IsClean.ShouldBeTrue();
            result.CurrentBranch.ShouldBe("main");
            result.DirtyPaths.ShouldBeEmpty();
            result.Error.ShouldBeNull();
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task Status_DirtyWorktree_ReportsDirtyPathsVerbatim()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            runner.WhenExact("git", ["-C", path, "--no-optional-locks", "status", "--porcelain"],
                new ProcessResult(0, " M src/foo.cs\n?? scratch.txt\n", ""));
            runner.WhenExact("git", ["-C", path, "branch", "--show-current"], new ProcessResult(0, "feature/3085\n", ""));

            var (exit, output) = await CaptureConsoleAsync(() => cmd.Status(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.IsClean.ShouldBeFalse();
            result.CurrentBranch.ShouldBe("feature/3085");
            result.DirtyPaths.ShouldBe(new[] { " M src/foo.cs", "?? scratch.txt" });
            result.Error.ShouldBeNull();
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task Status_DetachedHead_EmitsNullCurrentBranch()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            runner.WhenExact("git", ["-C", path, "--no-optional-locks", "status", "--porcelain"], new ProcessResult(0, "", ""));
            // git branch --show-current emits empty stdout when HEAD is detached
            runner.WhenExact("git", ["-C", path, "branch", "--show-current"], new ProcessResult(0, "\n", ""));

            var (exit, output) = await CaptureConsoleAsync(() => cmd.Status(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.IsClean.ShouldBeTrue();
            result.CurrentBranch.ShouldBeNull();
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    // ─── Failure routing ──────────────────────────────────────────────────

    [Fact]
    public async Task Status_NotARepo_SurfacesGitStderrInErrorField()
    {
        var (cmd, runner) = CreateCommand();
        var path = CreateTempWorktree();
        try
        {
            runner.WhenExact("git", ["-C", path, "--no-optional-locks", "status", "--porcelain"],
                new ProcessResult(128, "", "fatal: not a git repository: '.'\n"));

            var (exit, output) = await CaptureConsoleAsync(() => cmd.Status(path: path));

            exit.ShouldBe(ExitCodes.Success);
            var result = Parse(output);
            result.IsClean.ShouldBeFalse();
            result.Error!.ShouldContain("not a git repository");
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
