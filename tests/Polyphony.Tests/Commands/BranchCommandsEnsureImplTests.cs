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
/// Tests for <c>polyphony branch ensure-impl</c>. Idempotent impl-branch
/// materialization with base = enclosing merge group.
/// </summary>
public sealed class BranchCommandsEnsureImplTests : CommandTestBase
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

    private static void StubCheckoutTracking(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", "--track", $"origin/{branch}"], new ProcessResult(0, "", ""));

    private static void StubCreateBranch(FakeProcessRunner runner, string branch, string startPoint)
        => runner.WhenExact("git", ["checkout", "-b", branch, startPoint], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubFetch(FakeProcessRunner runner, string refspec)
        => runner.WhenExact("git", ["fetch", "origin", refspec], new ProcessResult(0, "", ""));

    [Fact]
    public async Task EnsureImpl_InvalidRootId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 0, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task EnsureImpl_InvalidItemId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: -5, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.Error!.ShouldContain("itemId");
    }

    [Fact]
    public async Task EnsureImpl_InvalidMgPath_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "INVALID"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task EnsureImpl_AllMissing_CreatesFromMgBaseAndPushes()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "impl/100-200", exists: false);
        StubLocalBranchExists(runner, "impl/100-200", exists: false);
        StubLsRemote(runner, "mg/100_core", exists: true);
        StubLocalBranchExists(runner, "mg/100_core", exists: true);
        StubCreateBranch(runner, "impl/100-200", "mg/100_core");
        StubPush(runner, "impl/100-200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.Branch.ShouldBe("impl/100-200");
        result.BaseBranch.ShouldBe("mg/100_core");
        result.Action.ShouldBe("created");
        result.Pushed.ShouldBeTrue();
        result.CreatedFrom.ShouldBe("mg/100_core");
        result.RootId.ShouldBe(100);
        result.ItemId.ShouldBe(200);
        result.MgPath.ShouldBe("core");
    }

    [Fact]
    public async Task EnsureImpl_BaseMgMissing_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "impl/100-200", exists: false);
        StubLocalBranchExists(runner, "impl/100-200", exists: false);
        StubLsRemote(runner, "mg/100_core", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.Error!.ShouldContain("ensure-mg");
    }

    [Fact]
    public async Task EnsureImpl_TargetExistsLocally_JustChecksOut()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "impl/100-200", exists: true);
        StubLocalBranchExists(runner, "impl/100-200", exists: true);
        StubCheckout(runner, "impl/100-200");
        StubLsRemote(runner, "mg/100_core", exists: true);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.Action.ShouldBe("checked_out");
        result.Pushed.ShouldBeFalse();
        result.RemoteExisted.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureImpl_BaseRemoteOnly_FetchesBeforeCreate()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "impl/100-200", exists: false);
        StubLocalBranchExists(runner, "impl/100-200", exists: false);
        StubLsRemote(runner, "mg/100_core", exists: true);
        StubLocalBranchExists(runner, "mg/100_core", exists: false);
        StubFetch(runner, "mg/100_core");
        StubCheckoutTracking(runner, "mg/100_core");
        StubCreateBranch(runner, "impl/100-200", "mg/100_core");
        StubPush(runner, "impl/100-200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.BaseFetched.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureImpl_NestedMgPath_BuildsCorrectBaseBranch()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "impl/100-200", exists: false);
        StubLocalBranchExists(runner, "impl/100-200", exists: false);
        StubLsRemote(runner, "mg/100_core_api", exists: true);
        StubLocalBranchExists(runner, "mg/100_core_api", exists: true);
        StubCreateBranch(runner, "impl/100-200", "mg/100_core_api");
        StubPush(runner, "impl/100-200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "core_api"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.BaseBranch.ShouldBe("mg/100_core_api");
        result.MgPath.ShouldBe("core_api");
    }

    [Fact]
    public async Task EnsureImpl_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "impl/100-200", exists: true);
        StubLocalBranchExists(runner, "impl/100-200", exists: true);
        StubCheckout(runner, "impl/100-200");
        StubLsRemote(runner, "mg/100_core", exists: true);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "core"));

        output.ShouldContain("\"branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"action\"");
        output.ShouldContain("\"remote_existed\"");
        output.ShouldContain("\"pushed\"");
        output.ShouldContain("\"base_remote_existed\"");
        output.ShouldContain("\"base_fetched\"");
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"item_id\"");
        output.ShouldContain("\"mg_path\"");
    }
}
