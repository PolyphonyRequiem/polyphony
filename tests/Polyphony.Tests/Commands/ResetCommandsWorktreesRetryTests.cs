using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class ResetCommandsWorktreesRetryTests : CommandTestBase, IDisposable
{
    private readonly string _tempRoot;
    private readonly string _commonDir;
    private readonly string _rootRunsRoot;
    private readonly string _worktreePath;

    public ResetCommandsWorktreesRetryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"polyphony-reset-worktrees-{Guid.NewGuid():N}");
        _commonDir = Path.Combine(_tempRoot, "repo", ".git");
        _rootRunsRoot = Path.Combine(_tempRoot, "repo-runs", "root-100");
        _worktreePath = Path.Combine(_rootRunsRoot, "feature-100");

        Directory.CreateDirectory(_commonDir);
        Directory.CreateDirectory(_worktreePath);
        File.WriteAllText(Path.Combine(_worktreePath, "placeholder.txt"), "x");
    }

    public override void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        base.Dispose();
    }

    private (ResetCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var pullRequestReader = new PullRequestReader(gh, null);
        var resolver = new RepoIdentityResolver(git);
        var planObserver = new PlanObserver(git, gh, new ThrowingAdoClient(), twig, resolver);
        var walker = new HierarchyWalker(Config, Repository);

        return (new ResetCommands(twig, git, pullRequestReader, planObserver, walker), runner);
    }

    private void StubSingleRootWorktree(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, _commonDir + "\n", string.Empty));
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0,
                $"worktree {_worktreePath}\nHEAD deadbeef\nbranch refs/heads/feature/100\n\n",
                string.Empty));
        runner.WhenExact("git", ["worktree", "prune"], new ProcessResult(0, string.Empty, string.Empty));
    }

    [Fact]
    public async Task ResetWorktrees_Execute_RetriesUntilThirdAttemptSucceeds()
    {
        var (cmd, runner) = CreateCommand();
        StubSingleRootWorktree(runner);
        runner.WhenStartsWithSequence(
            "git",
            ["worktree", "remove", "--force", _worktreePath],
            new ProcessResult(1, string.Empty, "Permission denied"),
            new ProcessResult(1, string.Empty, "Permission denied"),
            new ProcessResult(0, string.Empty, string.Empty));

        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.ResetWorktrees(root: 100, execute: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetWorktreesResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.RemovedWorktrees.Count.ShouldBe(1);
        result.FailedWorktrees.Count.ShouldBe(0);
        result.RemovedWorktrees[0].Path.ShouldBe(_worktreePath);

        runner.Invocations.Count(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 4
            && i.Arguments[0] == "worktree"
            && i.Arguments[1] == "remove"
            && i.Arguments[3] == _worktreePath).ShouldBe(3);
        runner.Invocations.ShouldContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 2
            && i.Arguments[0] == "worktree"
            && i.Arguments[1] == "prune");
    }

    [Fact]
    public async Task ResetWorktrees_Execute_TreatsMissingPathAfterFailedAttemptsAsRemoved()
    {
        var (cmd, runner) = CreateCommand();
        StubSingleRootWorktree(runner);
        var attempts = 0;
        runner.WhenAsync(
            (e, a) => e == "git"
                && a.Count >= 4
                && a[0] == "worktree"
                && a[1] == "remove"
                && a[3] == _worktreePath,
            (_, _) =>
            {
                attempts++;
                if (attempts == 2 && Directory.Exists(_worktreePath))
                {
                    Directory.Delete(_worktreePath, recursive: true);
                }

                return Task.FromResult(new ProcessResult(1, string.Empty, "Permission denied"));
            });

        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.ResetWorktrees(root: 100, execute: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetWorktreesResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.RemovedWorktrees.Count.ShouldBe(1);
        result.FailedWorktrees.Count.ShouldBe(0);
        attempts.ShouldBe(2);
        runner.Invocations.ShouldContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 2
            && i.Arguments[0] == "worktree"
            && i.Arguments[1] == "prune");
    }

    [Fact]
    public async Task ResetWorktrees_Execute_ReportsFailureWhenPathStillExistsAfterRetries()
    {
        var (cmd, runner) = CreateCommand();
        StubSingleRootWorktree(runner);
        runner.WhenStartsWithSequence(
            "git",
            ["worktree", "remove", "--force", _worktreePath],
            new ProcessResult(1, string.Empty, "Permission denied"),
            new ProcessResult(1, string.Empty, "Permission denied"),
            new ProcessResult(1, string.Empty, "Permission denied"));

        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.ResetWorktrees(root: 100, execute: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetWorktreesResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.RemovedWorktrees.Count.ShouldBe(0);
        result.FailedWorktrees.Count.ShouldBe(1);
        result.FailedWorktrees[0].Path.ShouldBe(_worktreePath);
        result.FailedWorktrees[0].Reason.ShouldContain("Permission denied");

        runner.Invocations.Count(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 4
            && i.Arguments[0] == "worktree"
            && i.Arguments[1] == "remove"
            && i.Arguments[3] == _worktreePath).ShouldBe(3);
        runner.Invocations.ShouldContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 2
            && i.Arguments[0] == "worktree"
            && i.Arguments[1] == "prune");
    }
}
