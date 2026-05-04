using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for the <c>polyphony plan</c> verb group. Verifies output shape,
/// routing-script exit-code convention (always 0), and capability filtering for
/// <c>plan depth-guard</c> and <c>plan next-child</c>.
/// </summary>
public sealed class PlanCommandsTests : CommandTestBase
{
    private PlanCommands CreateCommand() => new(new HierarchyWalker(Config, Repository));

    // ─────────────────────────────────────────────────────────────────────────
    // depth-guard
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DepthGuard_BelowCap_ReturnsAllowed()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.DepthGuard(depth: 2, maxDepth: 6));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.Allowed.ShouldBeTrue();
        result.Depth.ShouldBe(2);
        result.MaxDepth.ShouldBe(6);
        result.Remaining.ShouldBe(4);
        result.Message.ShouldBe("Depth 2 is within limit (max 6). 4 level(s) remaining.");
    }

    [Fact]
    public void DepthGuard_AtCap_ReturnsBlocked()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.DepthGuard(depth: 6, maxDepth: 6));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.Allowed.ShouldBeFalse();
        result.Remaining.ShouldBe(0);
        result.Message.ShouldBe("Recursion depth 6 reached maximum 6");
    }

    [Fact]
    public void DepthGuard_AboveCap_ReturnsBlocked()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.DepthGuard(depth: 9, maxDepth: 6));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public void DepthGuard_DefaultMaxDepth_IsSix()
    {
        // Mirrors the script default at scripts/depth-guard.ps1:22
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.DepthGuard(depth: 0));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanDepthGuardResult);
        result.ShouldNotBeNull();
        result.MaxDepth.ShouldBe(6);
        result.Allowed.ShouldBeTrue();
        result.Remaining.ShouldBe(6);
    }

    [Fact]
    public void DepthGuard_AlwaysExitsZero_RoutingScriptConvention()
    {
        // Routing scripts must never use exit code as a routing signal.
        var cmd = CreateCommand();

        var allowedExit = CaptureConsole(() => cmd.DepthGuard(0)).ExitCode;
        var blockedExit = CaptureConsole(() => cmd.DepthGuard(99)).ExitCode;

        allowedExit.ShouldBe(0);
        blockedExit.ShouldBe(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // next-child
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NextChild_FiltersToPlannableCapability()
    {
        // Default config: Epic and Issue are plannable; Task is not.
        var epic = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Root").WithState("New").Build();
        var issue1 = new WorkItemBuilder().WithId(101).WithType("Issue").WithTitle("Plannable Issue").WithState("New").WithParentId(100).Build();
        var task1 = new WorkItemBuilder().WithId(102).WithType("Task").WithTitle("Implementable Task").WithState("New").WithParentId(100).Build();
        var issue2 = new WorkItemBuilder().WithId(103).WithType("Issue").WithTitle("Another Issue").WithState("New").WithParentId(100).Build();
        await SeedAsync(epic, issue1, task1, issue2);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(100));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.HasPlannableChildren.ShouldBeTrue();
        result.Count.ShouldBe(2);
        result.ParentId.ShouldBe(100);
        result.PlannableChildren.Length.ShouldBe(2);
        result.PlannableChildren.Select(c => c.Id).ShouldBe(new[] { 101, 103 }, ignoreOrder: true);
        result.PlannableChildren.ShouldAllBe(c => c.Type == "Issue");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task NextChild_NoChildren_ReturnsEmpty()
    {
        var epic = new WorkItemBuilder().WithId(200).WithType("Epic").WithTitle("Childless").WithState("New").Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(200));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.HasPlannableChildren.ShouldBeFalse();
        result.Count.ShouldBe(0);
        result.PlannableChildren.ShouldBeEmpty();
        result.ParentId.ShouldBe(200);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task NextChild_OnlyImplementableChildren_ReturnsEmpty()
    {
        // All children are Tasks (not plannable), so result is empty.
        var epic = new WorkItemBuilder().WithId(300).WithType("Epic").WithTitle("Tasks Only").WithState("New").Build();
        var t1 = new WorkItemBuilder().WithId(301).WithType("Task").WithTitle("T1").WithState("New").WithParentId(300).Build();
        var t2 = new WorkItemBuilder().WithId(302).WithType("Task").WithTitle("T2").WithState("New").WithParentId(300).Build();
        await SeedAsync(epic, t1, t2);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(300));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.HasPlannableChildren.ShouldBeFalse();
        result.Count.ShouldBe(0);
        result.PlannableChildren.ShouldBeEmpty();
    }

    [Fact]
    public async Task NextChild_NotFound_ReturnsZeroExitWithErrorPayload()
    {
        // Routing-script convention: not-found is not a CacheError; emit empty payload + error.
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.NextChild(99_999));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.HasPlannableChildren.ShouldBeFalse();
        result.Count.ShouldBe(0);
        result.PlannableChildren.ShouldBeEmpty();
        result.ParentId.ShouldBe(99_999);
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error.ShouldContain("99999");
    }

    [Fact]
    public async Task NextChild_PreservesChildOrder()
    {
        // Children seeded in arbitrary order should appear in the order returned by the repository.
        var epic = new WorkItemBuilder().WithId(400).WithType("Epic").WithTitle("Ordered").WithState("New").Build();
        var issueA = new WorkItemBuilder().WithId(401).WithType("Issue").WithTitle("A").WithState("New").WithParentId(400).Build();
        var issueB = new WorkItemBuilder().WithId(402).WithType("Issue").WithTitle("B").WithState("New").WithParentId(400).Build();
        var issueC = new WorkItemBuilder().WithId(403).WithType("Issue").WithTitle("C").WithState("New").WithParentId(400).Build();
        await SeedAsync(epic, issueA, issueB, issueC);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.NextChild(400));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanNextChildResult);
        result.ShouldNotBeNull();
        result.PlannableChildren.Length.ShouldBe(3);
        // Titles round-trip — ensures we're populating both id and title correctly.
        result.PlannableChildren.Select(c => c.Title).ShouldBe(new[] { "A", "B", "C" }, ignoreOrder: true);
    }
}
