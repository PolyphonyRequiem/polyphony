using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class PrCommandsTests : CommandTestBase
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
        => runner.WhenExact("git", new[] { "remote", "get-url", "origin" }, new ProcessResult(0, url + "\n", ""));

    private static void StubLsRemoteHas(FakeProcessRunner runner, string remote, string pattern, bool exists)
        => runner.WhenExact("git", new[] { "ls-remote", "--heads", remote, pattern },
            new ProcessResult(0, exists ? "abc123\trefs/heads/whatever\n" : "", ""));

    private static void StubTwigShowTree(FakeProcessRunner runner, int id, string? title)
    {
        var json = title is null ? "" : $$"""{"title":"{{title}}","id":{{id}}}""";
        runner.WhenExact("twig", new[] { "show", id.ToString(), "--tree", "--output", "json" },
            new ProcessResult(title is null ? 1 : 0, json, ""));
    }

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", new[] { "pr", "list" }, new ProcessResult(0, "[]", ""));

    private static void StubPrListExisting(FakeProcessRunner runner, int number, string url)
        => runner.WhenStartsWith("gh", new[] { "pr", "list" },
            new ProcessResult(0, $$"""[{"number":{{number}},"url":"{{url}}","headRefName":"feature/x"}]""", ""));

    private static void StubPrCreate(FakeProcessRunner runner, string url)
        => runner.WhenStartsWith("gh", new[] { "pr", "create" }, new ProcessResult(0, url + "\n", ""));

    private static void StubPrCreateFailure(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", new[] { "pr", "create" }, new ProcessResult(1, "", "boom"));

    [Fact]
    public async Task CreateFeaturePr_MissingFeatureBranch_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(workItem: 100, featureBranch: "", targetBranch: "main"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr create-feature-pr");
        envelope.MissingArgs.ShouldContain("--feature-branch");
    }

    [Fact]
    public async Task CreateFeaturePr_RemoteBranchMissing_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCreateFeatureResult)!;
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error!.ShouldContain("does not exist");
        result.Created.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateFeaturePr_NoOriginUrl_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        runner.WhenExact("git", new[] { "remote", "get-url", "origin" }, new ProcessResult(1, "", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCreateFeatureResult)!;
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error!.ShouldContain("repo slug");
    }

    [Fact]
    public async Task CreateFeaturePr_HappyPath_CreatesAndReturnsUrl()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 100, "Add cool thing");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/42");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCreateFeatureResult)!;
        result.Created.ShouldBeTrue();
        result.PrNumber.ShouldBe(42);
        result.PrUrl.ShouldBe("https://github.com/PolyphonyRequiem/polyphony/pull/42");
        result.Title.ShouldBe("feat: Add cool thing AB#100");
        result.DescriptionSummary.ShouldContain("feature/100-x -> main");
    }

    [Fact]
    public async Task CreateFeaturePr_ExistingOpenPr_ReusesIt()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 100, "Add cool thing");
        StubPrListExisting(runner, 99, "https://github.com/PolyphonyRequiem/polyphony/pull/99");
        // No StubPrCreate — must NOT be called.

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCreateFeatureResult)!;
        result.Created.ShouldBeFalse();
        result.PrNumber.ShouldBe(99);
        result.PrUrl.ShouldBe("https://github.com/PolyphonyRequiem/polyphony/pull/99");
        result.DescriptionSummary.ShouldContain("Reusing");

        var createCalled = runner.Invocations.Any(i =>
            i.Executable == "gh" && i.Arguments.Count >= 2
            && i.Arguments[0] == "pr" && i.Arguments[1] == "create");
        createCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateFeaturePr_TitleSuppliedExplicitly_OverridesAutoGenerated()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        StubGitRemoteOrigin(runner, "git@github.com:PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 100, "Other title that should be ignored");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/7");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(
                workItem: 100, featureBranch: "feature/100-x", targetBranch: "main",
                title: "feat: explicit title AB#100"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCreateFeatureResult)!;
        result.Title.ShouldBe("feat: explicit title AB#100");
    }

    [Fact]
    public async Task CreateFeaturePr_TwigShowTreeFails_FallsBackToGenericTitle()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 100, title: null); // simulates non-zero exit
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/8");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCreateFeatureResult)!;
        result.Title.ShouldBe("feat: deliver work item #100 AB#100");
    }

    [Fact]
    public async Task CreateFeaturePr_GhCreateReturnsEmptyUrl_ReturnsRoutingFailure()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 100, "Title");
        StubPrListEmpty(runner);
        StubPrCreateFailure(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrCreateFeatureResult)!;
        result.Created.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateFeaturePr_OutputIsSnakeCase()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 100, "Title");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/1");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));

        output.ShouldContain("\"pr_number\"", Case.Sensitive);
        output.ShouldContain("\"pr_url\"", Case.Sensitive);
        output.ShouldContain("\"description_summary\"", Case.Sensitive);
        output.ShouldNotContain("\"PrNumber\"", Case.Sensitive);
        output.ShouldNotContain("\"PrUrl\"", Case.Sensitive);
    }
}
