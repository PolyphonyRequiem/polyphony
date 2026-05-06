using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony pr open-task-pr</c>. Opens (or reuses) the PR
/// promoting a task branch into its enclosing merge-group branch.
/// </summary>
public sealed class PrCommandsOpenTaskPrTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new Polyphony.Locking.RunLockStore(), new Polyphony.Locking.RunLockPathResolver(git)), runner);
    }

    private static void StubGitRemoteOrigin(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubLsRemoteHas(FakeProcessRunner runner, string pattern, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", pattern],
            new ProcessResult(0, exists ? "abc123\trefs/heads/whatever\n" : "", ""));

    private static void StubTwigShowTree(FakeProcessRunner runner, int id, string? title)
    {
        var json = title is null ? "" : $$"""{"title":"{{title}}","id":{{id}}}""";
        runner.WhenExact("twig", ["show", id.ToString(), "--tree", "--output", "json"],
            new ProcessResult(title is null ? 1 : 0, json, ""));
    }

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListExisting(FakeProcessRunner runner, int number, string url)
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0, $$"""[{"number":{{number}},"url":"{{url}}","headRefName":"task/100-200"}]""", ""));

    private static void StubPrCreate(FakeProcessRunner runner, string url)
        => runner.WhenStartsWith("gh", ["pr", "create"], new ProcessResult(0, url + "\n", ""));

    [Fact]
    public async Task OpenTaskPr_InvalidRootId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 0, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenTaskResult)!;
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task OpenTaskPr_InvalidItemId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 100, itemId: 0, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenTaskResult)!;
        result.Error!.ShouldContain("itemId");
    }

    [Fact]
    public async Task OpenTaskPr_HeadMissingOnRemote_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/task/100-200", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenTaskResult)!;
        result.Error!.ShouldContain("head branch");
    }

    [Fact]
    public async Task OpenTaskPr_BaseMgMissingOnRemote_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/task/100-200", exists: true);
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenTaskResult)!;
        result.Error!.ShouldContain("base branch");
    }

    [Fact]
    public async Task OpenTaskPr_HappyPath_CreatesPrWithMgBaseAndTwigTitle()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/task/100-200", exists: true);
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 200, "Add login form");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenTaskResult)!;
        result.Created.ShouldBeTrue();
        result.PrNumber.ShouldBe(200);
        result.HeadBranch.ShouldBe("task/100-200");
        result.BaseBranch.ShouldBe("mg/100_core");
        result.Title.ShouldBe("Add login form AB#200");
        result.RootId.ShouldBe(100);
        result.ItemId.ShouldBe(200);
        result.MgPath.ShouldBe("core");
    }

    [Fact]
    public async Task OpenTaskPr_TwigShowTreeFails_FallsBackToGenericTitle()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/task/100-200", exists: true);
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 200, title: null);
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenTaskResult)!;
        result.Title.ShouldBe("task #200");
    }

    [Fact]
    public async Task OpenTaskPr_ExistingOpenPr_ReusesIt()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/task/100-200", exists: true);
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 200, "Add login form");
        StubPrListExisting(runner, 33, "https://github.com/PolyphonyRequiem/polyphony/pull/33");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 100, itemId: 200, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenTaskResult)!;
        result.Created.ShouldBeFalse();
        result.PrNumber.ShouldBe(33);

        var createCalled = runner.Invocations.Any(i =>
            i.Executable == "gh" && i.Arguments.Count >= 2
            && i.Arguments[0] == "pr" && i.Arguments[1] == "create");
        createCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenTaskPr_NestedMgPath_BuildsCorrectBaseBranch()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/task/100-200", exists: true);
        StubLsRemoteHas(runner, "refs/heads/mg/100_core_api", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 200, "Add endpoint");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/201");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 100, itemId: 200, mgPath: "core_api"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenTaskResult)!;
        result.BaseBranch.ShouldBe("mg/100_core_api");
        result.MgPath.ShouldBe("core_api");
    }

    [Fact]
    public async Task OpenTaskPr_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/task/100-200", exists: true);
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 200, "Add login");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/200");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.OpenTaskPr(rootId: 100, itemId: 200, mgPath: "core"));

        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"title\"");
        output.ShouldContain("\"head_branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"item_id\"");
        output.ShouldContain("\"mg_path\"");
        output.ShouldContain("\"created\"");
    }
}
