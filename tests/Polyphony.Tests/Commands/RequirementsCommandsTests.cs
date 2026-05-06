using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Sdlc;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <see cref="RequirementsCommands.Derive"/>. The default
/// process config in <see cref="CommandTestBase"/> registers:
/// <list type="bullet">
///   <item><description><c>Epic</c> → facets <c>[plannable]</c></description></item>
///   <item><description><c>Issue</c> → facets <c>[plannable, implementable]</c></description></item>
///   <item><description><c>Task</c> → facets <c>[implementable]</c></description></item>
/// </list>
/// </summary>
public sealed class RequirementsCommandsTests : CommandTestBase
{
    private RequirementsCommands CreateCommand() => new(Repository, Config);

    [Fact]
    public async Task Derive_WorkItemNotFound_ReturnsCacheErrorExitCode()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Derive(999, decomposable: false));

        exitCode.ShouldBe(ExitCodes.CacheError);
        output.ShouldContain("not found");
    }

    [Fact]
    public async Task Derive_TypeNotInProcessConfig_ReturnsConfigErrorExitCode()
    {
        var item = new WorkItemBuilder()
            .WithId(200).WithType("Spike").WithTitle("Unknown type").WithState("To Do").Build();
        await SeedAsync(item);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Derive(200, decomposable: false));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        output.ShouldContain("Spike");
    }

    [Fact]
    public async Task Derive_PlannableLeaf_EmitsThreePlanRequirements()
    {
        var epic = new WorkItemBuilder()
            .WithId(100).WithType("Epic").WithTitle("Test Epic").WithState("To Do").Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Derive(100, decomposable: false));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RequirementsDeriveResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(100);
        result.WorkItemType.ShouldBe("Epic");
        result.Decomposable.ShouldBeFalse();
        result.RequirementSet.ShouldNotBeNull();
        var kinds = result.RequirementSet!.Items.Select(r => r.Kind).ToList();
        kinds.ShouldContain(RequirementKind.PlanAuthored);
        kinds.ShouldContain(RequirementKind.PlanReviewed);
        kinds.ShouldContain(RequirementKind.PlanPromoted);
        kinds.ShouldNotContain(RequirementKind.ChildrenSeeded);
    }

    [Fact]
    public async Task Derive_PlannableDecomposable_AddsChildrenSeededRequirement()
    {
        var epic = new WorkItemBuilder()
            .WithId(101).WithType("Epic").WithTitle("Decomposable").WithState("To Do").Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Derive(101, decomposable: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RequirementsDeriveResult);
        result.ShouldNotBeNull();
        result.Decomposable.ShouldBeTrue();
        result.RequirementSet.ShouldNotBeNull();
        var kinds = result.RequirementSet!.Items.Select(r => r.Kind).ToHashSet();
        kinds.ShouldContain(RequirementKind.ChildrenSeeded);
    }

    [Fact]
    public async Task Derive_ImplementableLeaf_EmitsImplementationMergedOnly()
    {
        var task = new WorkItemBuilder()
            .WithId(102).WithType("Task").WithTitle("Code change").WithState("To Do").Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Derive(102, decomposable: false));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RequirementsDeriveResult);
        result.ShouldNotBeNull();
        result.WorkItemType.ShouldBe("Task");
        result.RequirementSet.ShouldNotBeNull();
        result.RequirementSet!.Items.Count.ShouldBe(1);
        result.RequirementSet.Items[0].Kind.ShouldBe(RequirementKind.ImplementationMerged);
        result.RequirementSet.Items[0].Disposition.ShouldBe(Disposition.Needed);
    }

    [Fact]
    public async Task Derive_PlannableImplementableLeaf_EmitsBothFamilies()
    {
        var issue = new WorkItemBuilder()
            .WithId(103).WithType("Issue").WithTitle("Plan + code").WithState("To Do").Build();
        await SeedAsync(issue);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Derive(103, decomposable: false));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RequirementsDeriveResult);
        result.ShouldNotBeNull();
        result.RequirementSet.ShouldNotBeNull();
        var kinds = result.RequirementSet!.Items.Select(r => r.Kind).ToHashSet();
        kinds.ShouldContain(RequirementKind.PlanAuthored);
        kinds.ShouldContain(RequirementKind.ImplementationMerged);

        // Plan-promoted gates implementation when no children seeded.
        result.RequirementSet.Edges.ShouldContain(
            e => e.PrerequisiteKind == RequirementKind.PlanPromoted &&
                 e.DependentKind == RequirementKind.ImplementationMerged);
    }

    [Fact]
    public async Task Derive_FacetOrderFlag_PassedThroughToDeriver()
    {
        // Issue is plannable+implementable; facet_order is meaningless without
        // actionable, so we get a warning but valid output. Verifies the
        // comma-separated parsing works.
        var issue = new WorkItemBuilder()
            .WithId(104).WithType("Issue").WithTitle("Issue").WithState("To Do").Build();
        await SeedAsync(issue);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(
            () => cmd.Derive(104, decomposable: false, facetOrder: "implementable"));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RequirementsDeriveResult);
        result.ShouldNotBeNull();
        result.FacetOrder.ShouldNotBeNull();
        result.FacetOrder![0].ShouldBe("implementable");
        result.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Derive_ResultIncludesInputProvenance()
    {
        var epic = new WorkItemBuilder()
            .WithId(105).WithType("Epic").WithTitle("Provenance").WithState("To Do").Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.Derive(105, decomposable: true, actionableExecutor: ""));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.RequirementsDeriveResult);
        result.ShouldNotBeNull();
        result.Inputs.ShouldNotBeNull();
        result.Inputs.Decomposable.ShouldBe(RequirementsInputProvenance.Explicit);
        result.Inputs.FacetOrder.ShouldBe(RequirementsInputProvenance.NotApplicable);
        result.Inputs.ActionableExecutor.ShouldBe(RequirementsInputProvenance.NotApplicable);
    }

    [Fact]
    public async Task Derive_OutputFieldNamesAreSnakeCase()
    {
        var epic = new WorkItemBuilder()
            .WithId(106).WithType("Epic").WithTitle("Snake").WithState("To Do").Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Derive(106, decomposable: false));

        // Stable JSON contract — snake_case per PolyphonyJsonContext options.
        // `acceptance_criteria` is omitted when null (WhenWritingNull); deriver
        // always emits null at this layer, so it won't appear here. Verified
        // separately by structure of the typed deserialization in other tests.
        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"work_item_type\"");
        output.ShouldContain("\"requirement_set\"");
        output.ShouldContain("\"prerequisite_kind\"");
        output.ShouldContain("\"required_disposition\"");
    }
}
