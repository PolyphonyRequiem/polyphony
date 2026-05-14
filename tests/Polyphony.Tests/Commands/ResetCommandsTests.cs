using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony reset run</c>. Uses <see cref="FakeProcessRunner"/>
/// to stub twig and git invocations.
/// </summary>
public sealed class ResetCommandsTests : CommandTestBase
{
    private ResetCommands CreateCommand(FakeProcessRunner runner)
    {
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var statePaths = new PolyphonyStatePaths(git);
        return new ResetCommands(twig, Repository, git, walker, statePaths);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenStartsWith("twig", ["sync"], new ProcessResult(0, "{}", ""));

    private static void StubGitCommonDir(FakeProcessRunner runner, string dir)
        => runner.WhenExact("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, dir, ""));

    private static void StubEmptyBranches(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["branch", "--list"], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["branch", "-r"], new ProcessResult(0, "", ""));
    }

    // ─── Not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task Run_WorkItemNotFound_ReturnsCacheError()
    {
        var runner = new FakeProcessRunner();
        StubSync(runner);

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 999));

        exitCode.ShouldBe(ExitCodes.CacheError);
        output.ShouldContain("\"error\"");
        output.ShouldContain("999");
    }

    [Fact]
    public async Task Run_MissingRootId_ReturnsRoutingFailure()
    {
        var runner = new FakeProcessRunner();
        var cmd = CreateCommand(runner);
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Run());

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
    }

    // ─── Dry run ────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_EnumeratesArtifacts_NoMutations()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root; polyphony:planned; twig")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.Action.ShouldBe("planned");
        result.DryRun.ShouldBe(true);
        result.TagsRemoved.ShouldNotBeNull();
        result.TagsRemoved!.ShouldContain("polyphony:root");
        result.TagsRemoved.ShouldContain("polyphony:planned");
        result.TagsRemoved.ShouldNotContain("twig");

        // No twig patch should have been called
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    // ─── Needs confirmation ─────────────────────────────────────────────

    [Fact]
    public async Task Run_NoForceNoDryRun_EmitsNeedsConfirmation()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.Action.ShouldBe("needs_confirmation");
    }

    // ─── Force execution ────────────────────────────────────────────────

    [Fact]
    public async Task Run_Force_StripsTags_EmitsExecuted()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root; polyphony; twig")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, force: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.Action.ShouldBe("executed");
        result.TagsRemoved.ShouldNotBeNull();
        result.TagsRemoved!.ShouldContain("polyphony:root");
        result.TagsRemoved.ShouldContain("polyphony");
        result.TagsRemoved.ShouldNotContain("twig");

        // Verify twig patch was invoked
        runner.Invocations.ShouldContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    // ─── Branch enumeration ─────────────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_EnumeratesBranchesForRoot()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(42).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        runner.WhenExact("git", ["branch", "--list"],
            new ProcessResult(0, "  feature/42\n  impl/42-100\n  mg/42_alpha\n* main\n  plan/42\n  feature/99\n", ""));
        runner.WhenExact("git", ["branch", "-r"],
            new ProcessResult(0, "  origin/feature/42\n  origin/plan/42-200\n  origin/main\n", ""));

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 42, dryRun: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.LocalBranchesDeleted.ShouldNotBeNull();
        result.LocalBranchesDeleted!.ShouldContain("feature/42");
        result.LocalBranchesDeleted.ShouldContain("impl/42-100");
        result.LocalBranchesDeleted.ShouldContain("mg/42_alpha");
        result.LocalBranchesDeleted.ShouldContain("plan/42");
        result.LocalBranchesDeleted.ShouldNotContain("main");
        result.LocalBranchesDeleted.ShouldNotContain("feature/99");
        result.RemoteBranchesDeleted.ShouldNotBeNull();
        result.RemoteBranchesDeleted!.ShouldContain("feature/42");
        result.RemoteBranchesDeleted.ShouldContain("plan/42-200");
        result.RemoteBranchesDeleted.ShouldNotContain("main");
    }

    // ─── Descendant tag scrub ───────────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_IncludesDescendantTags()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build(),
            new WorkItemBuilder()
                .WithId(101).WithType("Issue").WithTitle("Child")
                .WithState("To Do").WithTags("polyphony; polyphony:planned")
                .WithParentId(100)
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);

        var cmd = CreateCommand(runner);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.ItemsPatched.ShouldNotBeNull();
        result.ItemsPatched!.ShouldContain(100);
        result.ItemsPatched.ShouldContain(101);
        result.TagsRemoved.ShouldNotBeNull();
        result.TagsRemoved!.ShouldContain("polyphony:root");
        result.TagsRemoved.ShouldContain("polyphony");
        result.TagsRemoved.ShouldContain("polyphony:planned");
    }

    // ─── JSON contract: snake_case ──────────────────────────────────────

    [Fact]
    public async Task Run_DryRun_SnakeCaseFieldNames()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);

        var cmd = CreateCommand(runner);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"action\"");
        output.ShouldContain("\"dry_run\"");
        // Ordinal checks: PascalCase keys must not leak
        output.Contains("\"RootId\"", StringComparison.Ordinal).ShouldBeFalse();
        output.Contains("\"DryRun\"", StringComparison.Ordinal).ShouldBeFalse();
        output.Contains("\"TagsRemoved\"", StringComparison.Ordinal).ShouldBeFalse();
        output.Contains("\"ItemsPatched\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    // ─── JSON contract: null fields omitted ─────────────────────────────

    [Fact]
    public async Task Run_NullFieldsOmitted_WhenWritingNull()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);

        var cmd = CreateCommand(runner);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        // Error should be null → omitted
        output.ShouldNotContain("\"error\"");
    }

    // ─── JSON contract: deserialization round-trip ───────────────────────

    [Fact]
    public async Task Run_DeserializationRoundTrip_FieldsMapped()
    {
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(100).WithType("Epic").WithTitle("Root")
                .WithState("To Do").WithTags("polyphony:root")
                .Build());

        var runner = new FakeProcessRunner();
        StubSync(runner);
        StubGitCommonDir(runner, "/fake/git-common");
        StubEmptyBranches(runner);

        var cmd = CreateCommand(runner);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Run(rootId: 100, dryRun: true));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetResult);
        result.ShouldNotBeNull();
        result.RootId.ShouldBe(100);
        result.Action.ShouldBe("planned");
        result.DryRun.ShouldBe(true);
    }
}
