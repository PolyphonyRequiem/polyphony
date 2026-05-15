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
/// Tests for <c>polyphony branch ensure-mg</c>. Idempotent merge-group
/// branch materialization with auto-derived base from <c>mgPath</c>.
/// </summary>
public sealed class BranchCommandsEnsureMgTests : CommandTestBase
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

    // Stubs that mirror IGitClient's actual call shapes so tests
    // can't drift from production code silently.
    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", branch],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubLocalBranchExists(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(exists ? 0 : 1, exists ? "abc123\n" : "", exists ? "" : "fatal: needed a single revision"));

    private static void StubCheckout(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));

    private static void StubCheckoutTracking(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", "--track", $"origin/{branch}"], new ProcessResult(0, "", ""));

    private static void StubCreateBranch(FakeProcessRunner runner, string branch, string startPoint)
        => runner.WhenExact("git", ["checkout", "-b", branch, startPoint], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubFetch(FakeProcessRunner runner, string refspec)
        => runner.WhenExact("git", ["fetch", "origin", refspec], new ProcessResult(0, "", ""));

    [Fact]
    public async Task EnsureMg_InvalidRootId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 0, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Error.ShouldNotBeNullOrEmpty();
        result.Action.ShouldBe("error");
    }

    [Fact]
    public async Task EnsureMg_NumericPathSegment_ReturnsConfigError()
    {
        // MergeGroupId grammar requires lowercase-letter prefix; bare numbers fail.
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "1"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task EnsureMg_DepthExceedsHardStop_ReturnsConfigError()
    {
        // 6-segment path exceeds hard-stop limit (5).
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureMergeGroup(rootId: 100, mgPath: "a_b_c_d_e_f"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.DepthExceeded.ShouldBeTrue();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task EnsureMg_TopLevelBranchAlreadyExistsLocally_ChecksOut()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core", exists: true);
        StubLocalBranchExists(runner, "mg/100_core", exists: true);
        StubCheckout(runner, "mg/100_core");
        StubLsRemote(runner, "feature/100", exists: true); // base existence check

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Branch.ShouldBe("mg/100_core");
        result.BaseBranch.ShouldBe("feature/100");
        result.Action.ShouldBe("checked_out");
        result.RemoteExisted.ShouldBeTrue();
        result.Pushed.ShouldBeFalse();
        result.RootId.ShouldBe(100);
        result.MgPath.ShouldBe("core");
        result.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task EnsureMg_TopLevelBaseLocalAndTargetMissing_CreatesAndPushes()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core", exists: false);
        StubLocalBranchExists(runner, "mg/100_core", exists: false);
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: true); // base is local
        StubCreateBranch(runner, "mg/100_core", "feature/100");
        StubPush(runner, "mg/100_core");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Action.ShouldBe("created");
        result.Pushed.ShouldBeTrue();
        result.BaseRemoteExisted.ShouldBeTrue();
        result.BaseFetched.ShouldBeFalse();
        result.CreatedFrom.ShouldBe("feature/100");
    }

    [Fact]
    public async Task EnsureMg_TopLevelBaseRemoteOnly_FetchesThenCreates()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core", exists: false);
        StubLocalBranchExists(runner, "mg/100_core", exists: false);
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: false); // base is remote-only
        StubFetch(runner, "feature/100");
        StubCheckoutTracking(runner, "feature/100");
        StubCreateBranch(runner, "mg/100_core", "feature/100");
        StubPush(runner, "mg/100_core");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Action.ShouldBe("created");
        result.BaseFetched.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureMg_BaseMissingOnRemote_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core", exists: false);
        StubLocalBranchExists(runner, "mg/100_core", exists: false);
        StubLsRemote(runner, "feature/100", exists: false); // base missing on remote

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error!.ShouldContain("does not exist on remote");
        result.Error.ShouldContain("ensure-feature");
    }

    [Fact]
    public async Task EnsureMg_NestedTargetMissing_DerivesParentMgAsBase()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core_api", exists: false);
        StubLocalBranchExists(runner, "mg/100_core_api", exists: false);
        StubLsRemote(runner, "mg/100_core", exists: true);
        StubLocalBranchExists(runner, "mg/100_core", exists: true);
        StubCreateBranch(runner, "mg/100_core_api", "mg/100_core");
        StubPush(runner, "mg/100_core_api");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core_api"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Branch.ShouldBe("mg/100_core_api");
        result.BaseBranch.ShouldBe("mg/100_core");
        result.Action.ShouldBe("created");
        result.Depth.ShouldBe(2);
    }

    [Fact]
    public async Task EnsureMg_NestedBaseMissing_PointsAtEnsureMgRemediation()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core_api", exists: false);
        StubLocalBranchExists(runner, "mg/100_core_api", exists: false);
        StubLsRemote(runner, "mg/100_core", exists: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core_api"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Error!.ShouldContain("ensure-mg");
        result.Error!.ShouldContain("parent path");
    }

    [Fact]
    public async Task EnsureMg_RemoteOnlyTarget_FetchesAndChecksOut()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core", exists: true);
        StubLocalBranchExists(runner, "mg/100_core", exists: false);
        StubFetch(runner, "mg/100_core");
        StubCheckoutTracking(runner, "mg/100_core");
        StubLsRemote(runner, "feature/100", exists: true);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Action.ShouldBe("checked_out");
        result.RemoteExisted.ShouldBeTrue();
        result.Pushed.ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureMg_LocalOnlyTarget_PushesToRemote()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core", exists: false);
        StubLocalBranchExists(runner, "mg/100_core", exists: true);
        StubCheckout(runner, "mg/100_core");
        StubPush(runner, "mg/100_core");
        StubLsRemote(runner, "feature/100", exists: true);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Action.ShouldBe("checked_out");
        result.Pushed.ShouldBeTrue();
        result.RemoteExisted.ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureMg_DepthThree_FlagsWarning()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_a_b_c", exists: true);
        StubLocalBranchExists(runner, "mg/100_a_b_c", exists: true);
        StubCheckout(runner, "mg/100_a_b_c");
        StubLsRemote(runner, "mg/100_a_b", exists: true);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "a_b_c"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureMergeGroupResult)!;
        result.Depth.ShouldBe(3);
        result.DepthWarning.ShouldBeTrue();
        result.DepthExceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureMg_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "mg/100_core", exists: true);
        StubLocalBranchExists(runner, "mg/100_core", exists: true);
        StubCheckout(runner, "mg/100_core");
        StubLsRemote(runner, "feature/100", exists: true);

        var (_, output) = await CaptureConsoleAsync(() => cmd.EnsureMergeGroup(rootId: 100, mgPath: "core"));

        // Lock the JSON wire contract that workflow YAMLs read.
        output.ShouldContain("\"branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"action\"");
        output.ShouldContain("\"remote_existed\"");
        output.ShouldContain("\"pushed\"");
        output.ShouldContain("\"base_remote_existed\"");
        output.ShouldContain("\"base_fetched\"");
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"mg_path\"");
        output.ShouldContain("\"depth\"");
        output.ShouldContain("\"depth_warning\"");
        output.ShouldContain("\"depth_exceeded\"");
    }
}
