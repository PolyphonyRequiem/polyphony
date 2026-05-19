using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Services;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony branch ensure-feature</c>. Focuses on the
/// four state combinations (local × remote ∈ {present, absent}) and
/// the AB#211 sibling-worktree case where <c>git checkout</c> refuses
/// because the branch is already checked out elsewhere — the verb
/// must treat that as a success (existence IS satisfied) and surface
/// the sibling worktree path on the envelope.
/// </summary>
public sealed class BranchCommandsEnsureFeatureTests : CommandTestBase
{
    private static (BranchCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var config = new ProcessConfigBuilder()
            .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>())
            .Build();
        var store = new SqliteCacheStore("Data Source=:memory:");
        var repo = new SqliteWorkItemRepository(store, new WorkItemMapper());
        var walker = new HierarchyWalker(config, repo);
        var validator = new TransitionValidator(config);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new BranchCommands(twig, walker, repo, validator, git, config, new Polyphony.Sdlc.Observers.RepoIdentityResolver(git), new Polyphony.Sdlc.Observers.PullRequestReader(gh, null)), runner);
    }

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", branch],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubLocalBranchExists(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(exists ? 0 : 1, exists ? "abc123\n" : "", exists ? "" : "fatal: needed a single revision"));

    private static void StubCheckout(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));

    private static void StubCheckoutFails(FakeProcessRunner runner, string branch, string stderr)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(128, "", stderr));

    private static void StubCheckoutTracking(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", "--track", $"origin/{branch}"], new ProcessResult(0, "", ""));

    private static void StubCreateBranch(FakeProcessRunner runner, string branch, string startPoint)
        => runner.WhenExact("git", ["checkout", "-b", branch, startPoint], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubFetch(FakeProcessRunner runner, string refspec)
        => runner.WhenExact("git", ["fetch", "origin", refspec], new ProcessResult(0, "", ""));

    // ─── Input validation ────────────────────────────────────────────────

    [Fact]
    public async Task EnsureFeature_MissingBranch_ReturnsRoutingFailure()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(() => cmd.EnsureFeature(branch: ""));
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    // ─── Local + remote both present (the happy idempotent case) ─────────

    [Fact]
    public async Task EnsureFeature_LocalAndRemoteBothExist_ChecksOutWithoutPush()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "feature/3043", exists: true);
        StubLocalBranchExists(runner, "feature/3043", exists: true);
        StubCheckout(runner, "feature/3043");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureFeature(branch: "feature/3043"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureFeatureResult)!;
        result.Action.ShouldBe("checked_out");
        result.RemoteExisted.ShouldBeTrue();
        result.Pushed.ShouldBeFalse();
        result.WorktreePath.ShouldBeNull();
    }

    // ─── AB#211: branch already checked out in a sibling worktree ────────

    [Fact]
    public async Task EnsureFeature_LocalExistsButHeldByOtherWorktree_ReturnsExistsInOtherWorktreeSuccess()
    {
        // Parallel-fleet apex convention: the feature branch lives in a
        // sibling worktree (e.g. polyphony-item-3043) so `git checkout`
        // in *this* worktree fails with exit 128. AB#211: existence IS
        // satisfied — we should succeed with the sibling path on the
        // envelope so downstream steps can route to it.
        var (cmd, runner) = CreateCommand();
        const string siblingPath = "C:/Users/dangreen/projects/polyphony-item-3043";
        StubLsRemote(runner, "feature/3043", exists: true);
        StubLocalBranchExists(runner, "feature/3043", exists: true);
        StubCheckoutFails(runner, "feature/3043",
            $"fatal: 'feature/3043' is already used by worktree at '{siblingPath}'\n");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureFeature(branch: "feature/3043"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureFeatureResult)!;
        result.Action.ShouldBe("exists_in_other_worktree");
        result.WorktreePath.ShouldBe(siblingPath);
        result.RemoteExisted.ShouldBeTrue();
        result.Pushed.ShouldBeFalse();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task EnsureFeature_LocalInOtherWorktreeAndRemoteAbsent_StillPushes()
    {
        // Branch lives in a sibling worktree but never made it to origin.
        // Push must still happen — git push operates on refs, not on the
        // working-tree state of the current worktree.
        var (cmd, runner) = CreateCommand();
        const string siblingPath = "/repos/polyphony-item-9001";
        StubLsRemote(runner, "feature/9001", exists: false);
        StubLocalBranchExists(runner, "feature/9001", exists: true);
        StubCheckoutFails(runner, "feature/9001",
            $"fatal: 'feature/9001' is already used by worktree at '{siblingPath}'\n");
        StubPush(runner, "feature/9001");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureFeature(branch: "feature/9001"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureFeatureResult)!;
        result.Action.ShouldBe("exists_in_other_worktree");
        result.WorktreePath.ShouldBe(siblingPath);
        result.RemoteExisted.ShouldBeFalse();
        result.Pushed.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureFeature_LocalExistsCheckoutFailsForOtherReason_ReturnsCacheError()
    {
        // Generic checkout failure (e.g. dirty working tree) is NOT the
        // AB#211 case — the regex doesn't match, the exception propagates
        // out of the verb's try, and the catch wraps it as CacheError.
        // Make sure the worktree-tolerant branch doesn't accidentally
        // swallow unrelated failures.
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "feature/7", exists: true);
        StubLocalBranchExists(runner, "feature/7", exists: true);
        StubCheckoutFails(runner, "feature/7",
            "error: Your local changes to the following files would be overwritten by checkout\n");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureFeature(branch: "feature/7"));

        exit.ShouldBe(ExitCodes.CacheError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureFeatureResult)!;
        result.Action.ShouldBe("error");
        result.Error.ShouldNotBeNullOrEmpty();
        result.WorktreePath.ShouldBeNull();
    }

    // ─── Remote-only and neither-exists baseline coverage ────────────────

    [Fact]
    public async Task EnsureFeature_RemoteOnly_FetchesAndTracks()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "feature/123", exists: true);
        StubLocalBranchExists(runner, "feature/123", exists: false);
        StubFetch(runner, "feature/123");
        StubCheckoutTracking(runner, "feature/123");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureFeature(branch: "feature/123"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureFeatureResult)!;
        result.Action.ShouldBe("checked_out");
        result.RemoteExisted.ShouldBeTrue();
        result.Pushed.ShouldBeFalse();
        result.WorktreePath.ShouldBeNull();
    }

    [Fact]
    public async Task EnsureFeature_NeitherExists_CreatesFromBase()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "feature/123", exists: false);
        StubLocalBranchExists(runner, "feature/123", exists: false);
        StubCreateBranch(runner, "feature/123", "main");
        StubPush(runner, "feature/123");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureFeature(branch: "feature/123"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureFeatureResult)!;
        result.Action.ShouldBe("created");
        result.CreatedFrom.ShouldBe("main");
        result.RemoteExisted.ShouldBeFalse();
        result.Pushed.ShouldBeTrue();
    }
}
