using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony pr open-mg-pr</c>. Opens (or reuses) the PR
/// promoting an MG branch into its parent (parent MG when nested,
/// feature when top-level).
/// </summary>
public sealed class PrCommandsOpenMgPrTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new Polyphony.Locking.RunLockStore(), new Polyphony.Locking.RunLockPathResolver(git), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git)), runner);
    }

    private static void StubGitRemoteOrigin(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubLsRemoteHas(FakeProcessRunner runner, string pattern, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", pattern],
            new ProcessResult(0, exists ? "abc123\trefs/heads/whatever\n" : "", ""));

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListExisting(FakeProcessRunner runner, int number, string url)
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0, $$"""[{"number":{{number}},"url":"{{url}}","headRefName":"mg/100_core"}]""", ""));

    private static void StubPrCreate(FakeProcessRunner runner, string url)
        => runner.WhenStartsWith("gh", ["pr", "create"], new ProcessResult(0, url + "\n", ""));

    [Fact]
    public async Task OpenMgPr_InvalidRootId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 0, mgPath: "core"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task OpenMgPr_InvalidMgPath_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 100, mgPath: "BAD"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task OpenMgPr_HeadMissingOnRemote_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.Error!.ShouldContain("head branch");
        result.HeadBranch.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task OpenMgPr_BaseMissingOnRemote_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: false);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.Error!.ShouldContain("base branch");
    }

    [Fact]
    public async Task OpenMgPr_NoSlug_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: true);
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(1, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.Error!.ShouldContain("repo slug");
    }

    [Fact]
    public async Task OpenMgPr_TopLevel_HappyPath_CreatesPrWithFeatureBase()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/77");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.Created.ShouldBeTrue();
        result.PrNumber.ShouldBe(77);
        result.PrUrl.ShouldBe("https://github.com/PolyphonyRequiem/polyphony/pull/77");
        result.HeadBranch.ShouldBe("mg/100_core");
        result.BaseBranch.ShouldBe("feature/100");
        result.Title.ShouldContain("merge group core for root #100");
        result.MgPath.ShouldBe("core");
    }

    [Fact]
    public async Task OpenMgPr_Nested_HappyPath_CreatesPrWithParentMgBase()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/mg/100_core_api", exists: true);
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/78");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 100, mgPath: "core_api"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.HeadBranch.ShouldBe("mg/100_core_api");
        result.BaseBranch.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task OpenMgPr_ExistingOpenPr_ReusesIt()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrListExisting(runner, 50, "https://github.com/PolyphonyRequiem/polyphony/pull/50");
        // No StubPrCreate — must NOT be called.

        var (exit, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 100, mgPath: "core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.Created.ShouldBeFalse();
        result.PrNumber.ShouldBe(50);

        var createCalled = runner.Invocations.Any(i =>
            i.Executable == "gh" && i.Arguments.Count >= 2
            && i.Arguments[0] == "pr" && i.Arguments[1] == "create");
        createCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenMgPr_TitleSuppliedExplicitly_OverridesFallback()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/79");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenMgPr(rootId: 100, mgPath: "core", title: "explicit MG title"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenMergeGroupResult)!;
        result.Title.ShouldBe("explicit MG title");
    }

    [Fact]
    public async Task OpenMgPr_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "refs/heads/mg/100_core", exists: true);
        StubLsRemoteHas(runner, "refs/heads/feature/100", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/79");

        var (_, output) = await CaptureConsoleAsync(() => cmd.OpenMgPr(rootId: 100, mgPath: "core"));

        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"title\"");
        output.ShouldContain("\"head_branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"mg_path\"");
        output.ShouldContain("\"created\"");
    }
}
