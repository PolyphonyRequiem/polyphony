using System.Text.Json;
using Polyphony.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony merge-group nesting-decision</c>. Verifies the
/// default-nest trigger from ADR <c>docs/decisions/branch-model.md</c>
/// (decomposable AND implementable -> nest) along with planner
/// overrides (<c>--override-flat</c>, <c>--override-nested-mg-id</c>),
/// the mutual-exclusion guard between them, derived nested-id naming,
/// nested path composition, and validation of the inputs.
/// </summary>
public sealed class MergeGroupCommandsNestingDecisionTests : CommandTestBase
{
    private static MergeGroupCommands CreateCommand() => new();

    private static async Task<MergeGroupNestingDecisionResult> InvokeAsync(
        Func<MergeGroupCommands, Task<int>> body,
        int expectedExit = 0)
    {
        var cmd = CreateCommand();
        // Manually capture stdout (MergeGroupCommands does not need the base SeedAsync
        // scaffolding — it's a pure function — but we still want the lock).
        var (exit, output) = await new MgConsoleCapture().RunAsync(() => body(cmd));
        exit.ShouldBe(expectedExit);
        return JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.MergeGroupNestingDecisionResult)!;
    }

    [Fact]
    public async Task NestingDecision_InvalidRootId_ReturnsConfigError()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(0, 200, "core", hasImplementable: true, decomposable: true),
            expectedExit: ExitCodes.ConfigError);
        result.Error!.ShouldContain("rootId");
        result.Decision.ShouldBe("error");
    }

    [Fact]
    public async Task NestingDecision_InvalidItemId_ReturnsConfigError()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(100, 0, "core", hasImplementable: true, decomposable: true),
            expectedExit: ExitCodes.ConfigError);
        result.Error!.ShouldContain("itemId");
    }

    [Fact]
    public async Task NestingDecision_InvalidParentMgPath_ReturnsConfigError()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(100, 200, "BAD!", hasImplementable: true, decomposable: true),
            expectedExit: ExitCodes.ConfigError);
        result.Error!.ShouldContain("merge-group path");
    }

    [Fact]
    public async Task NestingDecision_BothOverridesSet_ReturnsConfigError()
    {
        // ADR § Override: --override-flat and --override-nested-mg-id are
        // mutually exclusive on a given child.
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 200, "core",
                hasImplementable: true, decomposable: true,
                overrideFlat: true, overrideNestedMgId: "data"),
            expectedExit: ExitCodes.ConfigError);
        result.Error!.ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task NestingDecision_OverrideNestedMgIdInvalidGrammar_ReturnsConfigError()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 200, "core",
                hasImplementable: true, decomposable: true,
                overrideNestedMgId: "BAD!"),
            expectedExit: ExitCodes.ConfigError);
        result.Error!.ShouldContain("merge-group id");
    }

    // ─── Default trigger ─────────────────────────────────────────────────

    [Fact]
    public async Task NestingDecision_DecomposableAndImplementable_DefaultsToNestWithDerivedId()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 4567, "core",
                hasImplementable: true, decomposable: true));

        result.Decision.ShouldBe("nest");
        result.NestedMgId.ShouldBe("item-4567");
        result.NestedMgPath.ShouldBe("core_item-4567");
        result.ImplBranch.ShouldBeNull();
        result.OverrideApplied.ShouldBe("default");
        result.Reason.ShouldContain("default-nest");
    }

    [Fact]
    public async Task NestingDecision_NotDecomposable_DefaultsToFlat()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 4567, "core",
                hasImplementable: true, decomposable: false));

        result.Decision.ShouldBe("flat");
        result.NestedMgId.ShouldBeNull();
        result.NestedMgPath.ShouldBeNull();
        result.ImplBranch.ShouldBe("impl/100-4567");
        result.OverrideApplied.ShouldBe("default");
        result.Reason.ShouldContain("not decomposable");
    }

    [Fact]
    public async Task NestingDecision_NotImplementable_DefaultsToFlat()
    {
        // Even decomposable items don't nest if they aren't implementable —
        // pure organizational containers stay flat.
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 4567, "core",
                hasImplementable: false, decomposable: true));

        result.Decision.ShouldBe("flat");
        result.ImplBranch.ShouldBe("impl/100-4567");
        result.Reason.ShouldContain("not implementable");
    }

    [Fact]
    public async Task NestingDecision_NeitherFacet_DefaultsToFlat()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 4567, "core",
                hasImplementable: false, decomposable: false));

        result.Decision.ShouldBe("flat");
        result.ImplBranch.ShouldBe("impl/100-4567");
    }

    // ─── Overrides ───────────────────────────────────────────────────────

    [Fact]
    public async Task NestingDecision_OverrideFlat_ForcesFlatEvenWhenTriggerFires()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 4567, "core",
                hasImplementable: true, decomposable: true,
                overrideFlat: true));

        result.Decision.ShouldBe("flat");
        result.NestedMgId.ShouldBeNull();
        result.ImplBranch.ShouldBe("impl/100-4567");
        result.OverrideApplied.ShouldBe("flat");
        result.Reason.ShouldContain("planner override");
    }

    [Fact]
    public async Task NestingDecision_OverrideNestedMgId_NamesNestedMgExplicitly()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 4567, "core",
                hasImplementable: true, decomposable: true,
                overrideNestedMgId: "data-migrations"));

        result.Decision.ShouldBe("nest");
        result.NestedMgId.ShouldBe("data-migrations");
        result.NestedMgPath.ShouldBe("core_data-migrations");
        result.OverrideApplied.ShouldBe("nested-mg-id");
        result.Reason.ShouldContain("explicit nested mg id");
    }

    [Fact]
    public async Task NestingDecision_OverrideNestedMgId_EvenWhenTriggerWouldNotFire()
    {
        // Planner can name a nested MG even for a non-decomposable child.
        // The override is the source of truth.
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 4567, "core",
                hasImplementable: false, decomposable: false,
                overrideNestedMgId: "auth-rewrite"));

        result.Decision.ShouldBe("nest");
        result.NestedMgId.ShouldBe("auth-rewrite");
        result.OverrideApplied.ShouldBe("nested-mg-id");
    }

    // ─── Path composition ────────────────────────────────────────────────

    [Fact]
    public async Task NestingDecision_NestedParentPath_AppendsCorrectly()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 4567, "core_subgroup",
                hasImplementable: true, decomposable: true));

        result.NestedMgPath.ShouldBe("core_subgroup_item-4567");
    }

    [Fact]
    public async Task NestingDecision_DeepParentPath_AppendsCorrectly()
    {
        var result = await InvokeAsync(
            cmd => cmd.NestingDecision(
                100, 9999, "core_a_b_c",
                hasImplementable: true, decomposable: true,
                overrideNestedMgId: "leaf"));

        result.NestedMgPath.ShouldBe("core_a_b_c_leaf");
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task NestingDecision_JsonOutput_UsesSnakeCaseFieldNames()
    {
        var cmd = CreateCommand();
        var (_, output) = await new MgConsoleCapture().RunAsync(
            () => cmd.NestingDecision(100, 4567, "core", hasImplementable: true, decomposable: true));

        output.ShouldContain("\"root_id\"");
        output.ShouldContain("\"item_id\"");
        output.ShouldContain("\"parent_mg_path\"");
        output.ShouldContain("\"nested_mg_id\"");
        output.ShouldContain("\"nested_mg_path\"");
        output.ShouldContain("\"has_implementable\"");
        output.ShouldContain("\"override_applied\"");
        output.ShouldNotContain("\"RootId\"");
        output.ShouldNotContain("\"NestedMgId\"");
    }

    [Fact]
    public async Task NestingDecision_FlatDecision_OmitsNestedFields()
    {
        var cmd = CreateCommand();
        var (_, output) = await new MgConsoleCapture().RunAsync(
            () => cmd.NestingDecision(100, 200, "core", hasImplementable: false, decomposable: false));

        // Nullable fields are omitted from JSON output (JsonIgnoreCondition.WhenWritingNull).
        output.ShouldNotContain("nested_mg_id");
        output.ShouldNotContain("nested_mg_path");
        output.ShouldContain("impl_branch");
    }

    // ─── Console capture (avoids hard dependency on CommandTestBase wiring) ─

    /// <summary>
    /// Mini console-capture helper, modelled on
    /// <see cref="CommandTestBase.CaptureConsoleAsync"/>, that does not
    /// require the SQLite + repository scaffolding (this command is a
    /// pure function).
    /// </summary>
    private sealed class MgConsoleCapture
    {
        public async Task<(int ExitCode, string Output)> RunAsync(Func<Task<int>> body)
        {
            var oldOut = Console.Out;
            await using var ms = new MemoryStream();
            await using var writer = new StreamWriter(ms) { AutoFlush = true };
            await ConsoleTestLock.AsyncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Console.SetOut(writer);
                var exit = await body().ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                ms.Position = 0;
                using var reader = new StreamReader(ms);
                return (exit, reader.ReadToEnd());
            }
            finally
            {
                Console.SetOut(oldOut);
                ConsoleTestLock.AsyncLock.Release();
            }
        }
    }
}
