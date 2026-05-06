using System.Text.Json;
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
/// End-to-end tests for the <c>polyphony root</c> verb group. Covers
/// declare (delegates to scope mutation with the root tag) and resolve
/// (ancestor walk, fallback gate, cycle detection, walk-budget cap).
/// </summary>
public sealed class RootCommandsTests : CommandTestBase
{
    private (RootCommands Root, ScopeCommands Scope, FakeProcessRunner Runner) CreateCommand(ProcessConfig? cfg = null)
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var c = cfg ?? Config;
        var walker = new HierarchyWalker(c, Repository);
        var scope = new ScopeCommands(twig, Repository, walker);
        return (new RootCommands(twig, Repository, scope), scope, runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    // ─── declare ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Declare_NewItem_StampsRootTag_ChangedTrue()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).Build());
        var (root, _, runner) = CreateCommand();
        StubSync(runner);
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var (exit, output) = await CaptureConsoleAsync(() => root.Declare(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeMutationResult)!;
        result.Changed.ShouldBeTrue();
        result.TagsAfter.ShouldBe(["polyphony:root"]);
    }

    [Fact]
    public async Task Declare_AlreadyRoot_NoPatch_ChangedFalse()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("polyphony:root").Build());
        var (root, _, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => root.Declare(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ScopeMutationResult)!;
        result.Changed.ShouldBeFalse();
        runner.Invocations.ShouldNotContain(i => i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    // ─── resolve ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_SelfIsRoot_ReturnsSelf_NoFallback()
    {
        await SeedAsync(new WorkItemBuilder().WithId(1).WithTags("polyphony:root").Build());
        var (root, _, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => root.Resolve(workItem: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RootResolveResult)!;
        result.WorkItemId.ShouldBe(1);
        result.ResolvedRootId.ShouldBe(1);
        result.AncestorsWalked.ShouldBe([1]);
        result.FallbackRequired.ShouldBeFalse();
    }

    [Fact]
    public async Task Resolve_AncestorIsRoot_ReturnsAncestor_TracksWalkChain()
    {
        var grandparent = new WorkItemBuilder().WithId(10).WithTags("polyphony:root").Build();
        var parent = new WorkItemBuilder().WithId(20).WithParentId(10).Build();
        var child = new WorkItemBuilder().WithId(30).WithParentId(20).Build();
        await SeedAsync(grandparent, parent, child);

        var (root, _, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => root.Resolve(workItem: 30));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RootResolveResult)!;
        result.ResolvedRootId.ShouldBe(10);
        result.AncestorsWalked.ShouldBe([30, 20, 10]);
        result.FallbackRequired.ShouldBeFalse();
    }

    [Fact]
    public async Task Resolve_NoRootInChain_FallbackRequired_ExitSuccess()
    {
        // Walking off the top of the tree without finding a root tag is the
        // primary fallback-gate trigger. Exit MUST be 0 — the workflow routes
        // on `fallback_required`, not on the exit code.
        var parent = new WorkItemBuilder().WithId(10).Build();
        var child = new WorkItemBuilder().WithId(20).WithParentId(10).Build();
        await SeedAsync(parent, child);

        var (root, _, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => root.Resolve(workItem: 20));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RootResolveResult)!;
        result.ResolvedRootId.ShouldBeNull();
        result.AncestorsWalked.ShouldBe([20, 10]);
        result.FallbackRequired.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Resolve_BareTagIsNotRoot_KeepsWalking()
    {
        // Bare `polyphony` means in-scope, NOT root. A descendant with the bare
        // tag should keep walking to find the actual `polyphony:root`.
        var grandparent = new WorkItemBuilder().WithId(10).WithTags("polyphony:root").Build();
        var parent = new WorkItemBuilder().WithId(20).WithParentId(10).WithTags("polyphony").Build();
        var child = new WorkItemBuilder().WithId(30).WithParentId(20).WithTags("polyphony").Build();
        await SeedAsync(grandparent, parent, child);

        var (root, _, runner) = CreateCommand();
        StubSync(runner);

        var (_, output) = await CaptureConsoleAsync(() => root.Resolve(workItem: 30));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RootResolveResult)!;
        result.ResolvedRootId.ShouldBe(10);
        result.AncestorsWalked.ShouldBe([30, 20, 10]);
    }

    [Fact]
    public async Task Resolve_MissingItemInChain_FallbackRequired_NonZeroExit()
    {
        // Parent ID points to an unknown work item — treat as a hard failure.
        var orphan = new WorkItemBuilder().WithId(20).WithParentId(99).Build();
        await SeedAsync(orphan);

        var (root, _, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => root.Resolve(workItem: 20));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RootResolveResult)!;
        result.FallbackRequired.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("99");
    }

    [Fact]
    public async Task Resolve_WalkBudgetExceeded_FallbackRequired()
    {
        // 4 items deep: target → mid → upper → top, no root tag anywhere.
        // With maxAncestorWalk: 2, we should stop after walking the first two
        // and surface fallback-required.
        var top = new WorkItemBuilder().WithId(10).Build();
        var upper = new WorkItemBuilder().WithId(20).WithParentId(10).Build();
        var mid = new WorkItemBuilder().WithId(30).WithParentId(20).Build();
        var target = new WorkItemBuilder().WithId(40).WithParentId(30).Build();
        await SeedAsync(top, upper, mid, target);

        var (root, _, runner) = CreateCommand();
        StubSync(runner);

        var (exit, output) = await CaptureConsoleAsync(() => root.Resolve(workItem: 40, maxAncestorWalk: 2));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RootResolveResult)!;
        result.AncestorsWalked.Count.ShouldBe(2);
        result.FallbackRequired.ShouldBeTrue();
        result.ResolvedRootId.ShouldBeNull();
    }
}
