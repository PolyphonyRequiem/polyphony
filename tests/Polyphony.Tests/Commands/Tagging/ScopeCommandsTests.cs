using System.Text.Json;
using System.Text.Json.Nodes;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands.Tagging;

/// <summary>
/// End-to-end tests for the <c>polyphony scope</c> verb group. Verifies the
/// JSON contract for check / list / tag / untag, and the idempotency rule
/// that no twig patch is issued when the tag is already in the desired state.
/// </summary>
public sealed class ScopeCommandsTests : CommandTestBase
{
    private (ScopeCommands Command, FakeProcessRunner Runner) CreateCommand(ProcessConfig? cfg = null)
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var c = cfg ?? Config;
        var walker = new HierarchyWalker(c, Repository);
        return (new ScopeCommands(twig, Repository, walker), runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    // ─── check ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_NotFound_EmitsErrorAndNonZeroExit()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 999));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeCheckResult)!;
        result.WorkItemId.ShouldBe(999);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("999");
    }

    [Fact]
    public async Task Check_NoTags_ReturnsOutOfScope()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeCheckResult)!;
        result.InScope.ShouldBeFalse();
        result.IsRoot.ShouldBeFalse();
        result.Tags.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_BareTag_ReturnsInScopeNotRoot()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("polyphony").Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeCheckResult)!;
        result.InScope.ShouldBeTrue();
        result.IsRoot.ShouldBeFalse();
        result.Tags.ShouldBe(["polyphony"]);
    }

    [Fact]
    public async Task Check_RootTag_ReturnsInScopeAndRoot()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("polyphony:root").Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (_, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 1));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeCheckResult)!;
        result.InScope.ShouldBeTrue();
        result.IsRoot.ShouldBeTrue();
    }

    // ─── list ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_RootMissing_EmitsErrorAndNonZeroExit()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.List(rootId: 1));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeListResult)!;
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("1");
    }

    [Fact]
    public async Task List_PartitionsTaggedDescendantsFromUntagged()
    {
        // Root (always counts as in-scope for partitioning), one tagged descendant,
        // one untagged descendant. Verifies the polyphony-tag filter.
        var (root, children) = new WorkItemBuilder()
            .WithId(100).WithTags("polyphony:root")
            .WithChildren(
                new WorkItemBuilder().WithId(101).WithTags("polyphony"),
                new WorkItemBuilder().WithId(102))
            .BuildAll();
        await SeedAsync([root, .. children]);

        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.List(rootId: 100, maxDepth: 2));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeListResult)!;
        result.RootId.ShouldBe(100);
        result.InScopeItems.Select(i => i.Id).ShouldBe([100, 101], ignoreOrder: true);
        result.OutOfScopeItems.Select(i => i.Id).ShouldBe([102]);
        result.InScopeCount.ShouldBe(2);
        result.OutOfScopeCount.ShouldBe(1);
        result.InScopeItems.Single(i => i.Id == 100).IsRoot.ShouldBeTrue();
        result.InScopeItems.Single(i => i.Id == 101).IsRoot.ShouldBeFalse();
    }

    // ─── tag (idempotency contract) ─────────────────────────────────────────

    [Fact]
    public async Task Tag_AlreadyTagged_NoTwigPatchIssued_ChangedFalse()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("polyphony; twig").Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Tag(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeMutationResult)!;
        result.Changed.ShouldBeFalse();
        result.TagsBefore.ShouldBe(result.TagsAfter);

        // Critical: no `twig patch` invocation was made (only the sync).
        runner.Invocations.ShouldNotContain(i => i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task Tag_NotPresent_IssuesPatch_ChangedTrue()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("twig").Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        runner.WhenStartsWith("twig", ["patch", "--id", "1", "--json"], new ProcessResult(0, "{}", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Tag(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeMutationResult)!;
        result.Changed.ShouldBeTrue();
        result.TagsBefore.ShouldBe(["twig"]);
        result.TagsAfter.ShouldBe(["twig", "polyphony"]);

        var patchCall = runner.Invocations
            .Where(i => i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch")
            .ShouldHaveSingleItem();
        var json = JsonNode.Parse(patchCall.Arguments[4])!;
        json["System.Tags"]!.GetValue<string>().ShouldBe("twig; polyphony");
    }

    [Fact]
    public async Task Tag_TwigPatchFails_EmitsErrorAndNonZeroExit()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(1, "", "ado unreachable"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Tag(workItem: 1));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeMutationResult)!;
        result.Changed.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("twig patch failed");
    }

    // ─── untag ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Untag_NotPresent_NoTwigPatchIssued_ChangedFalse()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("twig").Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Untag(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeMutationResult)!;
        result.Changed.ShouldBeFalse();
        runner.Invocations.ShouldNotContain(i => i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task Untag_Present_IssuesPatch_ChangedTrue()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("polyphony; twig").Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Untag(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeMutationResult)!;
        result.Changed.ShouldBeTrue();
        result.TagsAfter.ShouldBe(["twig"]);
    }

    [Fact]
    public async Task Untag_DoesNotRemoveRootTag()
    {
        // `polyphony scope untag` only removes the BARE polyphony tag, never
        // polyphony:root. (root undeclare is a separate verb in a future phase.)
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("polyphony:root").Build());
        var (cmd, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Untag(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeMutationResult)!;
        result.Changed.ShouldBeFalse();
        result.TagsAfter.ShouldBe(["polyphony:root"]);
    }
}
