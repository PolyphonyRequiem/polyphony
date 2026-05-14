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

public sealed class BranchCommandsCheckZeroDiffTests : CommandTestBase
{
    private static (BranchCommands Command, FakeProcessRunner Runner) CreateCommand(
        string target = "main")
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var config = new ProcessConfigBuilder()
            .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>())
            .WithBranchStrategy(target: target)
            .Build();
        var store = new SqliteCacheStore("Data Source=:memory:");
        var repo = new SqliteWorkItemRepository(store, new WorkItemMapper());
        var walker = new HierarchyWalker(config, repo);
        var validator = new TransitionValidator(config);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new BranchCommands(twig, walker, repo, validator, git, gh, config), runner);
    }

    private static void StubFetch(FakeProcessRunner runner)
    {
        // git fetch matches any invocation starting with "git fetch"
        runner.WhenStartsWith("git", ["fetch"], new ProcessResult(0, "", ""));
    }

    private static void StubIsAncestorTrue(FakeProcessRunner runner, string feature, string target)
    {
        // git merge-base --is-ancestor exits 0 when true
        runner.WhenExact("git",
            ["merge-base", "--is-ancestor", $"origin/{feature}", $"origin/{target}"],
            new ProcessResult(0, "", ""));
    }

    private static void StubIsAncestorFalse(FakeProcessRunner runner, string feature, string target)
    {
        // git merge-base --is-ancestor exits 1 when false
        runner.WhenExact("git",
            ["merge-base", "--is-ancestor", $"origin/{feature}", $"origin/{target}"],
            new ProcessResult(1, "", ""));
    }

    private static BranchCheckZeroDiffResult Deserialize(string json)
        => JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.BranchCheckZeroDiffResult)!;

    [Fact]
    public async Task ZeroDiff_Detected_WhenFeatureIsAncestorOfTarget()
    {
        var (cmd, runner) = CreateCommand();
        StubFetch(runner);
        StubIsAncestorTrue(runner, "feature/3165", "main");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckZeroDiff(feature: "feature/3165"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.ZeroDiff.ShouldBeTrue();
        result.FeatureBranch.ShouldBe("feature/3165");
        result.TargetBranch.ShouldBe("main");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task NonZeroDiff_WhenFeatureHasCommitsAhead()
    {
        var (cmd, runner) = CreateCommand();
        StubFetch(runner);
        StubIsAncestorFalse(runner, "feature/3165", "main");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckZeroDiff(feature: "feature/3165"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.ZeroDiff.ShouldBeFalse();
        result.FeatureBranch.ShouldBe("feature/3165");
        result.TargetBranch.ShouldBe("main");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task TargetBranch_ReadFromProcessConfig()
    {
        var (cmd, runner) = CreateCommand(target: "develop");
        StubFetch(runner);
        StubIsAncestorTrue(runner, "feature/100", "develop");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckZeroDiff(feature: "feature/100"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.ZeroDiff.ShouldBeTrue();
        result.TargetBranch.ShouldBe("develop");
    }

    [Fact]
    public async Task MissingFeature_ReturnsHaltExitCode()
    {
        var (cmd, _) = CreateCommand();

        var (exit, _) = await CaptureConsoleAsync(() => cmd.CheckZeroDiff(feature: ""));

        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task GitError_ReturnsSuccessWithErrorField()
    {
        var (cmd, runner) = CreateCommand();
        // First fetch succeeds, second fetch throws via non-zero exit
        runner.WhenStartsWithSequence("git", ["fetch"],
            new ProcessResult(0, "", ""),
            new ProcessResult(128, "", "fatal: could not read from remote repository"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckZeroDiff(feature: "feature/3165"));

        // Routing-style: exit 0 even on error
        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.ZeroDiff.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task NoBranchStrategy_DefaultsToMain()
    {
        // Build config without explicit branch strategy
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var config = new ProcessConfigBuilder()
            .WithType("Issue", ["implementable"], new Dictionary<string, string>())
            .Build();
        var store = new SqliteCacheStore("Data Source=:memory:");
        var repo = new SqliteWorkItemRepository(store, new WorkItemMapper());
        var walker = new HierarchyWalker(config, repo);
        var validator = new TransitionValidator(config);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new BranchCommands(twig, walker, repo, validator, git, gh, config);

        StubFetch(runner);
        StubIsAncestorTrue(runner, "feature/42", "main");

        var (exit, output) = await CaptureConsoleAsync(() => cmd.CheckZeroDiff(feature: "feature/42"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.TargetBranch.ShouldBe("main");
        result.ZeroDiff.ShouldBeTrue();
    }
}
