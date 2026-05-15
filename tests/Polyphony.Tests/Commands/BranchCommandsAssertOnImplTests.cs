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
/// Tests for <c>polyphony branch assert-on-impl</c> — read-only HEAD
/// assertion that defends against AB#3210 (silent commit misroute when
/// the impl agent runs against a HEAD that does not match the assigned
/// task).
/// </summary>
public sealed class BranchCommandsAssertOnImplTests : CommandTestBase
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

    private static void StubCurrentBranch(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["branch", "--show-current"],
            new ProcessResult(0, branch + "\n", ""));

    private static void StubCurrentBranchFails(FakeProcessRunner runner)
        => runner.WhenExact("git", ["branch", "--show-current"],
            new ProcessResult(128, "", "fatal: not a git repository"));

    [Fact]
    public async Task AssertOnImpl_MissingRootId_ReturnsRoutingFailure()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(itemId: 200));
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task AssertOnImpl_MissingItemId_ReturnsRoutingFailure()
    {
        var (cmd, _) = CreateCommand();
        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 100));
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task AssertOnImpl_InvalidRootId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 0, itemId: 200));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchAssertOnImplResult)!;
        result.Action.ShouldBe("error");
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task AssertOnImpl_InvalidItemId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 100, itemId: -5));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchAssertOnImplResult)!;
        result.Action.ShouldBe("error");
        result.Error!.ShouldContain("itemId");
    }

    [Fact]
    public async Task AssertOnImpl_HeadOnExpectedBranch_EmitsOk()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "impl/100-200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 100, itemId: 200));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchAssertOnImplResult)!;
        result.Action.ShouldBe("ok");
        result.ExpectedBranch.ShouldBe("impl/100-200");
        result.ActualBranch.ShouldBe("impl/100-200");
        result.RootId.ShouldBe(100);
        result.ItemId.ShouldBe(200);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task AssertOnImpl_HeadOnSiblingImplBranch_EmitsMismatch()
    {
        // The exact reproducer for AB#3210 — agent dispatched for task
        // 3175 found HEAD on impl/3165-3176 (sibling task's branch).
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "impl/3165-3176");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 3165, itemId: 3175));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchAssertOnImplResult)!;
        result.Action.ShouldBe("mismatch");
        result.ExpectedBranch.ShouldBe("impl/3165-3175");
        result.ActualBranch.ShouldBe("impl/3165-3176");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task AssertOnImpl_HeadOnMgBranch_EmitsMismatch()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "mg/100_core");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 100, itemId: 200));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchAssertOnImplResult)!;
        result.Action.ShouldBe("mismatch");
        result.ExpectedBranch.ShouldBe("impl/100-200");
        result.ActualBranch.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task AssertOnImpl_DetachedHead_EmitsMismatchWithEmptyActual()
    {
        var (cmd, runner) = CreateCommand();
        // git branch --show-current emits an empty string when HEAD is detached.
        StubCurrentBranch(runner, "");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 100, itemId: 200));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchAssertOnImplResult)!;
        result.Action.ShouldBe("mismatch");
        result.ExpectedBranch.ShouldBe("impl/100-200");
        result.ActualBranch.ShouldBe("");
    }

    [Fact]
    public async Task AssertOnImpl_GitInvocationFails_EmitsMismatch()
    {
        // GetCurrentBranchAsync swallows non-zero exits and returns null;
        // the verb treats that as a mismatch with empty actual (rather
        // than masking it as 'ok').
        var (cmd, runner) = CreateCommand();
        StubCurrentBranchFails(runner);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 100, itemId: 200));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchAssertOnImplResult)!;
        result.Action.ShouldBe("mismatch");
        result.ActualBranch.ShouldBe("");
    }

    [Fact]
    public async Task AssertOnImpl_JsonContract_PreservesSnakeCaseKeys()
    {
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "impl/100-200");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.AssertOnImpl(rootId: 100, itemId: 200));

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        root.GetProperty("action").GetString().ShouldBe("ok");
        root.GetProperty("expected_branch").GetString().ShouldBe("impl/100-200");
        root.GetProperty("actual_branch").GetString().ShouldBe("impl/100-200");
        root.GetProperty("root_id").GetInt32().ShouldBe(100);
        root.GetProperty("item_id").GetInt32().ShouldBe(200);
    }
}
