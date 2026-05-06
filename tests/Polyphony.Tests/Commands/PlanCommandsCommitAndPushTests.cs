using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class PlanCommandsCommitAndPushTests : CommandTestBase
{
    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner)), runner);
    }

    private static PlanCommitAndPushResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanCommitAndPushResult)!;

    // ─── Stubs ────────────────────────────────────────────────────────────

    private static void StubCurrentBranch(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["branch", "--show-current"],
            new ProcessResult(0, branch + "\n", ""));

    private static void StubCheckout(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));

    private static void StubAdd(FakeProcessRunner runner, string pathspec)
        => runner.WhenExact("git", ["add", "--", pathspec], new ProcessResult(0, "", ""));

    private static void StubAddFails(FakeProcessRunner runner, string pathspec, string stderr)
        => runner.WhenExact("git", ["add", "--", pathspec], new ProcessResult(128, "", stderr));

    private static void StubStatus(FakeProcessRunner runner, string porcelain)
        => runner.WhenExact("git", ["status", "--porcelain"], new ProcessResult(0, porcelain, ""));

    private static void StubCommit(FakeProcessRunner runner, string message)
        => runner.WhenExact("git", ["commit", "-m", message], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubRevParse(FakeProcessRunner runner, string branch, string sha)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(0, sha + "\n", ""));

    // ─── Argument validation ──────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_BlankBranch_ConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "", message: "msg", paths: "x.md"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("branch is required");
    }

    [Fact]
    public async Task CommitAndPush_BlankMessage_ConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "", paths: "x.md"));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("message is required");
    }

    [Fact]
    public async Task CommitAndPush_BlankPaths_ConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "msg", paths: ""));
        exit.ShouldBe(ExitCodes.ConfigError);
        Parse(output).Error!.ShouldContain("paths is required");
    }

    // ─── Happy paths ──────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_SinglePath_StagesCommitsPushes()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, "plans/plan-100.md");
        // After staging, status shows the file as staged-modified ("M ").
        StubStatus(runner, "M  plans/plan-100.md\n");
        StubCommit(runner, "plan: 100");
        StubPush(runner, "plan/100");
        StubRevParse(runner, "plan/100", "abc1234deadbeef");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: "plans/plan-100.md"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Branch.ShouldBe("plan/100");
        result.Pushed.ShouldBeTrue();
        result.FilesStaged.ShouldBe(1);
        result.CommitSha.ShouldBe("abc1234deadbeef");
        result.NoOpReason.ShouldBeNull();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task CommitAndPush_MultiplePaths_AllStaged()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "plan/100-101");
        StubAdd(runner, "plans/plan-101.md");
        StubAdd(runner, "plans/plan-101-supporting.md");
        // Both files staged → two `M ` rows.
        StubStatus(runner, "M  plans/plan-101.md\nM  plans/plan-101-supporting.md\n");
        StubCommit(runner, "plan: 101");
        StubPush(runner, "plan/100-101");
        StubRevParse(runner, "plan/100-101", "deadbeefabc1234");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(
                branch: "plan/100-101",
                message: "plan: 101",
                paths: "plans/plan-101.md, plans/plan-101-supporting.md"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeTrue();
        result.FilesStaged.ShouldBe(2);
        result.CommitSha.ShouldBe("deadbeefabc1234");
    }

    [Fact]
    public async Task CommitAndPush_OnDifferentBranch_ChecksOutFirst()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "main");
        StubCheckout(runner, "plan/100");
        StubAdd(runner, "plans/plan-100.md");
        StubStatus(runner, "M  plans/plan-100.md\n");
        StubCommit(runner, "plan: 100");
        StubPush(runner, "plan/100");
        StubRevParse(runner, "plan/100", "abc1234");

        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: "plans/plan-100.md"));

        exit.ShouldBe(ExitCodes.Success);
        runner.Invocations.ShouldContain(c => c.Executable == "git" && c.Arguments.SequenceEqual(new[] { "checkout", "plan/100" }));
    }

    // ─── No-op idempotency ────────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_NothingStaged_NoOpSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, "plans/plan-100.md");
        // Status shows nothing staged — file is already committed at HEAD.
        StubStatus(runner, "");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: "plans/plan-100.md"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeFalse();
        result.FilesStaged.ShouldBe(0);
        result.NoOpReason.ShouldBe("no_changes");
        result.CommitSha.ShouldBeNull();
        result.Error.ShouldBeNull();

        // No commit + push were executed.
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "commit");
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "push");
    }

    [Fact]
    public async Task CommitAndPush_OnlyUnstagedChanges_NoOpSuccess()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, "plans/plan-100.md");
        // Untracked + unstaged-modified files should not trigger a commit.
        // (First column space = unstaged; '?' = untracked.)
        StubStatus(runner, " M other-file.txt\n?? scratch.md\n");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: "plans/plan-100.md"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeFalse();
        result.NoOpReason.ShouldBe("no_changes");
    }

    // ─── Failures ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_AddFails_RoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "plan/100");
        StubAddFails(runner, "plans/missing.md", "fatal: pathspec 'plans/missing.md' did not match any files");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: "plans/missing.md"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = Parse(output);
        result.Pushed.ShouldBeFalse();
        result.Error!.ShouldContain("did not match any files");
    }

    [Fact]
    public async Task CommitAndPush_CheckoutFails_RoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "main");
        runner.WhenExact("git", ["checkout", "plan/100"],
            new ProcessResult(1, "", "error: pathspec 'plan/100' did not match any file(s) known to git"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: "plans/plan-100.md"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        Parse(output).Error!.ShouldContain("did not match any file");
    }
}
