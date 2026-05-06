using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class WorktreeCommandsAddTests : CommandTestBase
{
    private static (WorktreeCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        return (new WorktreeCommands(new GitClient(runner)), runner);
    }

    private static WorktreeAddResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeAddResult)!;

    // ─── Argument validation ──────────────────────────────────────────────

    [Fact]
    public async Task Add_BlankBranch_EmitsErrorButExitsZero()
    {
        var (cmd, runner) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Add(branch: "", path: "C:/wt/foo"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error!.ShouldContain("branch is required");
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Add_BlankPath_EmitsErrorButExitsZero()
    {
        var (cmd, runner) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Add(branch: "feature/x", path: ""));

        exit.ShouldBe(ExitCodes.Success);
        Parse(output).Error!.ShouldContain("path is required");
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Happy paths ──────────────────────────────────────────────────────

    [Fact]
    public async Task Add_NoRef_PassesGitWorktreeAddWithoutRef()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "add", "-b", "feature/x", "C:/wt/x"],
            new ProcessResult(0, "Preparing worktree...\n", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Add(branch: "feature/x", path: "C:/wt/x"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Branch.ShouldBe("feature/x");
        result.Path.ShouldBe("C:/wt/x");
        result.GitRef.ShouldBeNull();
        result.Error.ShouldBeNull();

        runner.Invocations.Count.ShouldBe(1);
        runner.Invocations[0].Arguments.ShouldBe(new[] { "worktree", "add", "-b", "feature/x", "C:/wt/x" });
    }

    [Fact]
    public async Task Add_WithRef_AppendsRefArg()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "add", "-b", "feature/x", "C:/wt/x", "origin/main"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Add(branch: "feature/x", path: "C:/wt/x", gitRef: "origin/main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.GitRef.ShouldBe("origin/main");
        result.Error.ShouldBeNull();

        runner.Invocations[0].Arguments.ShouldBe(
            new[] { "worktree", "add", "-b", "feature/x", "C:/wt/x", "origin/main" });
    }

    // ─── Failure routing ──────────────────────────────────────────────────

    [Fact]
    public async Task Add_GitFails_RoutesViaErrorFieldAndStillExitsZero()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "add", "-b", "feature/x", "C:/wt/x"],
            new ProcessResult(128, "", "fatal: '/wt/x' already exists\n"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Add(branch: "feature/x", path: "C:/wt/x"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error!.ShouldContain("already exists");
        result.Branch.ShouldBe("feature/x");
        result.Path.ShouldBe("C:/wt/x");
    }

    [Fact]
    public async Task Add_GitFailsWithEmptyStderr_FallsBackToExitCodeMessage()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "add", "-b", "feature/x", "C:/wt/x"],
            new ProcessResult(2, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Add(branch: "feature/x", path: "C:/wt/x"));

        exit.ShouldBe(ExitCodes.Success);
        Parse(output).Error!.ShouldContain("exited with code 2");
    }
}
