using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Phase 3 P7a — unit tests for <see cref="PlanCommands.DeriveAncestorChain"/>.
/// Covers the root-plan special case, direct-child-of-root, deep descendants,
/// and the four error paths (item not found, broken chain, cycle, walk-limit).
/// </summary>
public sealed class PlanCommandsDeriveAncestorChainTests : CommandTestBase
{
    private PlanCommands CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return new PlanCommands(walker, Repository, Config, twig);
    }

    private static PlanDeriveAncestorChainResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDeriveAncestorChainResult)!;

    [Fact]
    public async Task RootId_NonPositive_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(0, 250));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--root-id must be positive");
    }

    [Fact]
    public async Task ItemId_NonPositive_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, -5));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--item-id must be positive");
    }

    [Fact]
    public async Task RootPlan_ItemEqualsRoot_EmptyChain()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.IsRootPlan.ShouldBeTrue();
        result.RootId.ShouldBe(100);
        result.ItemId.ShouldBe(100);
        result.ParentItemId.ShouldBeNull();
        result.AncestorIds.ShouldBe(string.Empty);
        result.AncestorChain.ShouldBeEmpty();
        result.Depth.ShouldBe(0);
    }

    [Fact]
    public async Task DirectChildOfRoot_ChainIsRootOnly_ParentItemIdNull()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Root").Build(),
            new WorkItemBuilder().WithId(250).WithType("Issue").WithParentId(100).Build());

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 250));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.IsRootPlan.ShouldBeFalse();
        result.RootId.ShouldBe(100);
        result.ItemId.ShouldBe(250);
        // Direct child of root: --parent-item-id is omitted (null) per the
        // plan-PR verbs' contract; --ancestor-ids is "root".
        result.ParentItemId.ShouldBeNull();
        result.AncestorIds.ShouldBe("root");
        result.AncestorChain.ShouldBe(["root"]);
        result.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task Grandchild_ChainIncludesParent()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Root").Build(),
            new WorkItemBuilder().WithId(250).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(300).WithType("Task").WithParentId(250).Build());

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 300));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ParentItemId.ShouldBe(250);
        result.AncestorIds.ShouldBe("250,root");
        result.AncestorChain.ShouldBe(["250", "root"]);
        result.Depth.ShouldBe(2);
    }

    [Fact]
    public async Task GreatGrandchild_FullChain()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(300).WithType("Issue").WithParentId(200).Build(),
            new WorkItemBuilder().WithId(400).WithType("Task").WithParentId(300).Build());

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 400));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ParentItemId.ShouldBe(300);
        result.AncestorIds.ShouldBe("300,200,root");
        result.AncestorChain.ShouldBe(["300", "200", "root"]);
        result.Depth.ShouldBe(3);
    }

    [Fact]
    public async Task ItemNotFound_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 999));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("Work item 999 not found");
    }

    [Fact]
    public async Task ItemHasNoParent_NotDescendantOfRoot_EmitsError()
    {
        // 250 has no parent — it isn't reachable from root 100.
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(250).WithType("Issue").WithParentId(null).Build());

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 250));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("not a descendant of root");
    }

    [Fact]
    public async Task ChainTerminatesAtNonRoot_EmitsError()
    {
        // 300's parent is 200, but 200 has no parent — chain dead-ends without
        // ever reaching root 100.
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(null).Build(),
            new WorkItemBuilder().WithId(300).WithType("Task").WithParentId(200).Build());

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 300));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("not a descendant of root");
    }

    [Fact]
    public async Task BrokenAncestorChain_AncestorMissingFromRepo_EmitsError()
    {
        // 300's parent points at 200, but 200 isn't in the repo.
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(300).WithType("Task").WithParentId(200).Build());

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 300));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("Ancestor work item 200 not found");
    }

    [Fact]
    public async Task CycleDetected_EmitsError()
    {
        // 200 ↔ 300 form a parent cycle. The walk should detect 200 reappearing.
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(300).Build(),
            new WorkItemBuilder().WithId(300).WithType("Issue").WithParentId(200).Build());

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.DeriveAncestorChain(100, 200));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("Cycle detected");
    }
}
