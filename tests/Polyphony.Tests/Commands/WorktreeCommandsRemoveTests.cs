using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class WorktreeCommandsRemoveTests : CommandTestBase
{
    private static (WorktreeCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        return (new WorktreeCommands(new GitClient(runner)), runner);
    }

    private static WorktreeRemoveResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeRemoveResult)!;

    // ─── Argument validation ──────────────────────────────────────────────

    [Fact]
    public async Task Remove_BlankPath_EmitsErrorButExitsZero()
    {
        var (cmd, runner) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Remove(path: ""));

        exit.ShouldBe(ExitCodes.Success);
        Parse(output).Error!.ShouldContain("path is required");
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Happy paths ──────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_DefaultForce_OmitsForceArg()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "remove", "C:/wt/x"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Remove(path: "C:/wt/x"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Path.ShouldBe("C:/wt/x");
        result.Force.ShouldBeFalse();
        result.Error.ShouldBeNull();

        runner.Invocations[0].Arguments.ShouldBe(new[] { "worktree", "remove", "C:/wt/x" });
    }

    [Fact]
    public async Task Remove_WithForce_PassesForceFlag()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "remove", "--force", "C:/wt/x"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Remove(path: "C:/wt/x", force: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Force.ShouldBeTrue();
        result.Error.ShouldBeNull();

        runner.Invocations[0].Arguments.ShouldBe(new[] { "worktree", "remove", "--force", "C:/wt/x" });
    }

    // ─── Failure routing ──────────────────────────────────────────────────

    [Fact]
    public async Task Remove_GitFails_SurfacesStderrAndStillExitsZero()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "remove", "C:/wt/x"],
            new ProcessResult(128, "", "fatal: 'C:/wt/x' contains modified or untracked files, use --force\n"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Remove(path: "C:/wt/x"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error!.ShouldContain("modified or untracked");
        result.Path.ShouldBe("C:/wt/x");
        result.Force.ShouldBeFalse();
    }

    [Fact]
    public async Task Remove_GitFailsWithEmptyStderr_FallsBackToExitCodeMessage()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "remove", "C:/wt/x"],
            new ProcessResult(5, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.Remove(path: "C:/wt/x"));

        exit.ShouldBe(ExitCodes.Success);
        Parse(output).Error!.ShouldContain("exited with code 5");
    }
}
