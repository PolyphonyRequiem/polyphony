using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Sdlc;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony edges check</c>. Seeds work items
/// into the in-memory SQLite cache (via <see cref="CommandTestBase"/>),
/// drives the verb, and asserts on the JSON envelope it emits on
/// stdout (and the optional Markdown report on stderr).
///
/// <para>Cycle conflict end-to-end coverage lives at the
/// <see cref="EdgeGraph"/> layer (<c>EdgeGraphTests</c>) — PR #1's
/// definitional bucket cannot induce cycles by construction (children
/// unblock goes parent→child; terminal rollup goes child→parent on a
/// distinct requirement kind). Reproducing one through this verb would
/// require either an unreachable test seam at the verb surface or a
/// crafted requirement-set that doesn't match anything the deriver
/// actually emits — both add noise without strengthening the contract.
/// Same story for unknown-item: the verb-built input map only ever
/// contains items it walked itself.</para>
/// </summary>
public sealed class EdgesCheckCommandTests : CommandTestBase
{
    private EdgesCommands CreateCommand() => new(Repository, Config);

    /// <summary>
    /// Runs the verb while capturing both stdout and stderr separately.
    /// CommandTestBase.CaptureConsoleAsync only captures stdout — for the
    /// <c>--render text</c> branch we need to assert on stderr too.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> CaptureBothAsync(Func<Task<int>> action)
    {
        await ConsoleTestLock.AsyncLock.WaitAsync();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var origOut = Console.Out;
            var origErr = Console.Error;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            try
            {
                var exit = await action();
                return (exit, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(origOut);
                Console.SetError(origErr);
            }
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }

    private static EdgesCheckResult ParseJson(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.EdgesCheckResult)!;

    // ─── Input validation ────────────────────────────────────────────────

    [Fact]
    public async Task WorkItem_NonPositive_EmitsInvalidArgument()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 0));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNull();
        result.HasConflicts.ShouldBeFalse();
        result.Conflicts.ShouldBeEmpty();
        result.ItemsWalked.ShouldBe(0);
    }

    [Fact]
    public async Task Depth_Negative_EmitsInvalidArgument()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 100, depth: -1));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("invalid_argument");
    }

    // ─── Walk: missing root / unknown type ──────────────────────────────

    [Fact]
    public async Task WorkItem_NotFound_EmitsWorkItemNotFoundEnvelope_ExitsZero()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 999));
        exit.ShouldBe(ExitCodes.Success);  // Always exit 0; route via envelope.
        var result = ParseJson(output);
        result.WorkItemId.ShouldBe(999);
        result.ErrorCode.ShouldBe("work_item_not_found");
        result.Error.ShouldNotBeNullOrEmpty();
        result.ItemsWalked.ShouldBe(0);
        result.EdgesTotal.ShouldBe(0);
        result.HasConflicts.ShouldBeFalse();
    }

    [Fact]
    public async Task WorkItem_UnknownType_EmitsTypeUnknownEnvelope()
    {
        // CommandTestBase config registers Epic / Issue / Task. "Bug" is unknown.
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Bug").Build());
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ErrorCode.ShouldBe("type_unknown");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("Bug");
    }

    // ─── Happy path: trees ──────────────────────────────────────────────

    [Fact]
    public async Task SingleItem_NoConflicts_HasConflictsFalse()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.WorkItemId.ShouldBe(100);
        result.ItemsWalked.ShouldBe(1);
        result.HasConflicts.ShouldBeFalse();
        result.Conflicts.ShouldBeEmpty();
        result.Error.ShouldBeNull();
        result.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task ThreeDeepDag_NoConflicts_EmitsItemsAndEdges()
    {
        // Epic 100 → Issue 200 → Task 310. The definitional deriver
        // produces children-unblock + terminal-rollup edges across
        // each parent-child pair → 4 cross-item edges total
        // (2 per pair: 100→200 and 200→310).
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(310).WithType("Task").WithParentId(200).Build());

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = ParseJson(output);
        result.ItemsWalked.ShouldBe(3);
        result.EdgesTotal.ShouldBeGreaterThan(0);
        result.HasConflicts.ShouldBeFalse();
        result.Conflicts.ShouldBeEmpty();
    }

    // ─── Depth handling ─────────────────────────────────────────────────

    [Fact]
    public async Task Depth_One_WalksRootAndImmediateChildrenOnly()
    {
        // Tree: 100 → 200 → 310 → 410. With --depth 1 we expect only
        // 100 + 200, dropping the grand-grandchild.
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(310).WithType("Task").WithParentId(200).Build(),
            new WorkItemBuilder().WithId(410).WithType("Task").WithParentId(310).Build());

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 100, depth: 1));
        var result = ParseJson(output);
        result.ItemsWalked.ShouldBe(2);
    }

    [Fact]
    public async Task Depth_Zero_IsUnlimited()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build(),
            new WorkItemBuilder().WithId(310).WithType("Task").WithParentId(200).Build(),
            new WorkItemBuilder().WithId(410).WithType("Task").WithParentId(310).Build());

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 100, depth: 0));
        var result = ParseJson(output);
        result.ItemsWalked.ShouldBe(4);
    }

    // ─── JSON envelope shape ────────────────────────────────────────────

    [Fact]
    public async Task JsonEnvelope_SnakeCaseFieldNames_PresentAndRoundTrips()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build());

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 100));

        // Round-trips through the source-gen context.
        var roundTripped = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.EdgesCheckResult);
        roundTripped.ShouldNotBeNull();
        roundTripped!.WorkItemId.ShouldBe(100);

        // snake_case wire keys (PolyphonyJsonContext convention).
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"items_walked\"");
        output.ShouldContain("\"edges_total\"");
        output.ShouldContain("\"has_conflicts\"");
        output.ShouldContain("\"conflicts\"");

        // No PascalCase leakage.
        output.Contains("\"WorkItemId\"").ShouldBeFalse();
        output.Contains("\"ItemsWalked\"").ShouldBeFalse();
        output.Contains("\"EdgesTotal\"").ShouldBeFalse();
        output.Contains("\"HasConflicts\"").ShouldBeFalse();

        // Null fields omitted on success (no Error / ErrorCode keys).
        output.Contains("\"error\"").ShouldBeFalse();
        output.Contains("\"error_code\"").ShouldBeFalse();
    }

    [Fact]
    public async Task JsonEnvelope_OnError_ContainsErrorAndErrorCode()
    {
        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Check(workItem: 0));
        output.ShouldContain("\"error\"");
        output.ShouldContain("\"error_code\"");
        output.ShouldContain("\"invalid_argument\"");
    }

    // ─── --render text ──────────────────────────────────────────────────

    [Fact]
    public async Task RenderText_EmitsMarkdownToStderr_StdoutKeepsJson()
    {
        await SeedAsync(
            new WorkItemBuilder().WithId(100).WithType("Epic").Build(),
            new WorkItemBuilder().WithId(200).WithType("Issue").WithParentId(100).Build());

        var cmd = CreateCommand();
        var (exit, stdout, stderr) = await CaptureBothAsync(() => cmd.Check(workItem: 100, render: "text"));
        exit.ShouldBe(ExitCodes.Success);

        // Stdout: JSON envelope (deserializable, snake_case).
        var result = JsonSerializer.Deserialize(stdout, PolyphonyJsonContext.Default.EdgesCheckResult);
        result.ShouldNotBeNull();
        result!.WorkItemId.ShouldBe(100);

        // Stderr: Markdown report.
        stderr.ShouldContain("# Edge Conflict Report");
        stderr.ShouldContain("work item 100");
        stderr.ShouldContain("**Items walked:**");
        stderr.ShouldContain("**Edges:**");
        stderr.ShouldContain("**Conflicts:**");
    }

    [Fact]
    public async Task RenderJson_EmitsNothingToStderr()
    {
        await SeedAsync(new WorkItemBuilder().WithId(100).WithType("Epic").Build());
        var cmd = CreateCommand();
        var (_, stdout, stderr) = await CaptureBothAsync(() => cmd.Check(workItem: 100, render: "json"));
        stdout.ShouldNotBeNullOrEmpty();
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenderText_OnError_StillEmitsMarkdownStub()
    {
        var cmd = CreateCommand();
        var (_, _, stderr) = await CaptureBothAsync(() => cmd.Check(workItem: 999, render: "text"));
        stderr.ShouldContain("# Edge Conflict Report");
        stderr.ShouldContain("**Error:**");
        stderr.ShouldContain("work_item_not_found");
    }
}
