using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Branching;

/// <summary>
/// Focused tests for <see cref="BranchEnsurer"/>. The five
/// <c>BranchCommands.Ensure*</c> verb test files already exercise the
/// matrix end-to-end through their journal/envelope wrappers; these
/// tests cover the shared class directly so the matrix's behavior is
/// pinned independent of any one verb's translation layer.
/// </summary>
public sealed class BranchEnsurerTests
{
    private static BranchEnsurer Create(FakeProcessRunner runner)
        => new(new GitClient(runner));

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", branch],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubRevParse(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(exists ? 0 : 1, exists ? "abc123\n" : "", exists ? "" : "fatal: needed a single revision"));

    private static void StubCurrentBranch(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["rev-parse", "--abbrev-ref", "HEAD"],
            new ProcessResult(0, $"{branch}\n", ""));

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

    // ─── Local + remote both exist — pure checkout, no base probe ────────

    [Fact]
    public async Task EnsureAsync_LocalAndRemoteBothExist_ChecksOut()
    {
        var runner = new FakeProcessRunner();
        StubLsRemote(runner, "feature/1", exists: true);
        StubRevParse(runner, "feature/1", exists: true);
        StubCurrentBranch(runner, "main");
        StubCheckout(runner, "feature/1");

        var outcome = await Create(runner).EnsureAsync(
            new BranchSpec("feature/1", "main", "origin",
                TolerateWorktreeConflict: true,
                IncludeBaseOnRemoteCheck: false),
            CancellationToken.None);

        outcome.Status.ShouldBe(BranchEnsureStatus.Success);
        outcome.Action.ShouldBe("checked_out");
        outcome.RemoteExisted.ShouldBeTrue();
        outcome.Pushed.ShouldBeFalse();
        outcome.WasMutated.ShouldBeTrue(); // currentBranch (main) != target (feature/1)
        outcome.WorktreePath.ShouldBeNull();
    }

    // ─── Remote only — fetch + tracking checkout ─────────────────────────

    [Fact]
    public async Task EnsureAsync_RemoteOnly_FetchesAndTracks()
    {
        var runner = new FakeProcessRunner();
        StubLsRemote(runner, "feature/2", exists: true);
        StubRevParse(runner, "feature/2", exists: false);
        StubFetch(runner, "feature/2");
        StubCheckoutTracking(runner, "feature/2");

        var outcome = await Create(runner).EnsureAsync(
            new BranchSpec("feature/2", "main", "origin",
                IncludeBaseOnRemoteCheck: false),
            CancellationToken.None);

        outcome.Status.ShouldBe(BranchEnsureStatus.Success);
        outcome.Action.ShouldBe("checked_out");
        outcome.RemoteExisted.ShouldBeTrue();
        outcome.WasMutated.ShouldBeTrue();
        outcome.BaseRemoteExisted.ShouldBeFalse();
        outcome.BaseFetched.ShouldBeFalse();
    }

    // ─── Neither exists, base missing on remote → BaseMissingOnRemote ────

    [Fact]
    public async Task EnsureAsync_NeitherExistsAndBaseMissingOnRemote_ReturnsBaseMissing()
    {
        var runner = new FakeProcessRunner();
        StubLsRemote(runner, "plan/9-7", exists: false);
        StubRevParse(runner, "plan/9-7", exists: false);
        StubLsRemote(runner, "plan/9", exists: false);

        var outcome = await Create(runner).EnsureAsync(
            new BranchSpec("plan/9-7", "plan/9", "origin"),
            CancellationToken.None);

        outcome.Status.ShouldBe(BranchEnsureStatus.BaseMissingOnRemote);
        outcome.Action.ShouldBe("error");
        outcome.MissingBase.ShouldBe("plan/9");
        outcome.MissingBaseRemote.ShouldBe("origin");
    }

    // ─── Neither exists, base on remote not local → fetch base + create ─

    [Fact]
    public async Task EnsureAsync_NeitherExistsAndBaseNeedsFetch_FetchesBaseAndCreatesTarget()
    {
        var runner = new FakeProcessRunner();
        StubLsRemote(runner, "plan/9-7", exists: false);
        StubRevParse(runner, "plan/9-7", exists: false);
        StubLsRemote(runner, "plan/9", exists: true);
        StubRevParse(runner, "plan/9", exists: false);
        StubFetch(runner, "plan/9");
        StubCheckoutTracking(runner, "plan/9");
        StubCreateBranch(runner, "plan/9-7", "plan/9");
        StubPush(runner, "plan/9-7");

        var outcome = await Create(runner).EnsureAsync(
            new BranchSpec("plan/9-7", "plan/9", "origin"),
            CancellationToken.None);

        outcome.Status.ShouldBe(BranchEnsureStatus.Success);
        outcome.Action.ShouldBe("created");
        outcome.RemoteExisted.ShouldBeFalse();
        outcome.Pushed.ShouldBeTrue();
        outcome.CreatedFrom.ShouldBe("plan/9");
        outcome.BaseRemoteExisted.ShouldBeTrue();
        outcome.BaseFetched.ShouldBeTrue();
        outcome.WasMutated.ShouldBeTrue();
    }

    // ─── Worktree conflict with tolerance ON + remote absent → still push ─

    [Fact]
    public async Task EnsureAsync_LocalInOtherWorktreeAndRemoteAbsent_StillPushesWithMutation()
    {
        // AB#211 + the post-rubber-duck pin: worktree-conflict tolerance
        // returns exists_in_other_worktree, but if the branch hasn't been
        // pushed yet the ensurer still pushes — and wasMutated flips true.
        var runner = new FakeProcessRunner();
        const string sibling = "C:/repos/sibling";
        StubLsRemote(runner, "feature/3", exists: false);
        StubRevParse(runner, "feature/3", exists: true);
        StubCurrentBranch(runner, "main");
        StubCheckoutFails(runner, "feature/3", $"fatal: 'feature/3' is already used by worktree at '{sibling}'\n");
        StubPush(runner, "feature/3");

        var outcome = await Create(runner).EnsureAsync(
            new BranchSpec("feature/3", "main", "origin",
                TolerateWorktreeConflict: true,
                IncludeBaseOnRemoteCheck: false),
            CancellationToken.None);

        outcome.Status.ShouldBe(BranchEnsureStatus.Success);
        outcome.Action.ShouldBe("exists_in_other_worktree");
        outcome.WorktreePath.ShouldBe(sibling);
        outcome.Pushed.ShouldBeTrue();
        outcome.WasMutated.ShouldBeTrue();
    }

    // ─── Worktree conflict with tolerance OFF → exception propagates ─────

    [Fact]
    public async Task EnsureAsync_LocalInOtherWorktreeAndToleranceOff_ThrowsExternalToolException()
    {
        // Plan/Impl/MG/Evidence verbs don't opt into worktree tolerance —
        // the exception must propagate so their catch block can surface a
        // CacheError envelope.
        var runner = new FakeProcessRunner();
        StubLsRemote(runner, "plan/9", exists: true);
        StubRevParse(runner, "plan/9", exists: true);
        StubCurrentBranch(runner, "main");
        StubCheckoutFails(runner, "plan/9", "fatal: 'plan/9' is already used by worktree at '/tmp/sibling'\n");

        var ensurer = Create(runner);
        await Should.ThrowAsync<ExternalToolException>(() =>
            ensurer.EnsureAsync(
                new BranchSpec("plan/9", "feature/9", "origin"),
                CancellationToken.None));
    }
}
