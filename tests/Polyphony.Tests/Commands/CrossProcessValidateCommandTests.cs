using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end cross-process template tests for <see cref="ValidateCommand"/>.
/// Exercises the full pipeline (work item seeding → in-memory SQLite → ValidateCommand → JSON output)
/// for each of the 4 process templates (Basic, Agile, Scrum, CMMI).
/// </summary>
[Trait("Category", "Slow")] // see #286 — forks polyphony.exe per test
public sealed class CrossProcessValidateCommandTests : CommandTestBase
{
    /// <summary>
    /// Defines the state names and transition targets for a single process template.
    /// </summary>
    private sealed record TemplateDefinition(
        string Name,
        string TopType,
        string MiddleType,
        string LeafType,
        string ProposedState,
        string InProgressState,
        string CompletedState,
        string BeginPlanningTarget,
        string ImplementationCompleteTarget);

    private static readonly TemplateDefinition[] Templates =
    [
        new("Basic", "Epic", "Issue", "Task", "To Do", "Doing", "Done", "Doing", "Done"),
        new("Agile", "Epic", "User Story", "Task", "New", "Active", "Closed", "Active", "Closed"),
        new("Scrum", "Epic", "Product Backlog Item", "Task", "New", "Committed", "Done", "Committed", "Done"),
        new("CMMI", "Epic", "Requirement", "Task", "Proposed", "Active", "Closed", "Active", "Closed"),
    ];

    public static IEnumerable<object[]> AllTemplateNames =>
        Templates.Select(t => new object[] { t.Name });

    private static TemplateDefinition GetTemplate(string name) =>
        Templates.First(t => t.Name == name);

    private ValidateCommand CreateCommand(TemplateDefinition template)
    {
        var transitions = new Dictionary<string, string>
        {
            ["begin_planning"] = template.BeginPlanningTarget,
            ["implementation_complete"] = template.ImplementationCompleteTarget,
        };

        var config = new ProcessConfigBuilder()
            .WithProcessTemplate(template.Name)
            .WithType(template.TopType, ["plannable"], transitions)
            .WithType(template.MiddleType, ["plannable", "implementable"], transitions)
            .WithType(template.LeafType, ["implementable"], transitions)
            .Build();

        return new ValidateCommand(new TransitionValidator(config), Repository);
    }

    // --- Scenario 1: Legal transition — begin_planning on Proposed item → is_valid: true ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Validate_LegalTransition_ReturnsSuccessWithIsValidTrue(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(1000)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic Proposed")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(1000, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1000);
        result.Event.ShouldBe("begin_planning");
        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.BeginPlanningTarget);
    }

    // --- Scenario 2: Illegal transition — begin_planning on InProgress item → is_valid: false ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Validate_IllegalTransition_ReturnsRoutingFailureWithIsValidFalse(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(1100)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic InProgress")
            .WithState(t.InProgressState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(1100, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1100);
        result.Event.ShouldBe("begin_planning");
        result.IsValid.ShouldBeFalse();
        result.TargetState.ShouldBe(t.BeginPlanningTarget);
        result.Message.ShouldNotBeNullOrEmpty();
        result.Message!.ShouldContain("begin_planning");
    }

    // --- Scenario 3: Unknown event — nonexistent_event → is_valid: false with error message ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Validate_UnknownEvent_ReturnsRoutingFailureWithErrorMessage(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(1200)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic Unknown Event")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(1200, "nonexistent_event"));

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1200);
        result.Event.ShouldBe("nonexistent_event");
        result.IsValid.ShouldBeFalse();
        result.Message.ShouldNotBeNullOrEmpty();
        result.Message!.ShouldContain("nonexistent_event");
    }

    // --- Scenario 4: Legal implementation_complete on InProgress item → is_valid: true ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Validate_ImplementationComplete_OnInProgressItem_ReturnsIsValidTrue(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(1300)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic ImplComplete")
            .WithState(t.InProgressState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(1300, "implementation_complete"));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(1300);
        result.Event.ShouldBe("implementation_complete");
        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.ImplementationCompleteTarget);
    }

    // --- Scenario 5: Illegal implementation_complete on Proposed item → is_valid: false ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Validate_ImplementationComplete_OnProposedItem_ReturnsIsValidFalse(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(1400)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Epic ImplComplete Illegal")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(1400, "implementation_complete"));

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
        result.TargetState.ShouldBe(t.ImplementationCompleteTarget);
        result.Message.ShouldNotBeNullOrEmpty();
        result.Message!.ShouldContain("implementation_complete");
    }

    // --- Scenario 6: JSON output uses snake_case per PolyphonyJsonContext ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Validate_OutputUsesSnakeCasePropertyNames(string templateName)
    {
        var t = GetTemplate(templateName);
        var epic = new WorkItemBuilder()
            .WithId(1500)
            .WithType(t.TopType)
            .WithTitle($"{templateName} Snake Case")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand(t);
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(1500, "begin_planning"));

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"is_valid\"");
        output.ShouldContain("\"target_state\"");
    }

    // --- Scenario 7: Middle-tier legal transition ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Validate_MiddleTierLegalTransition_ReturnsIsValidTrue(string templateName)
    {
        var t = GetTemplate(templateName);
        var middle = new WorkItemBuilder()
            .WithId(1600)
            .WithType(t.MiddleType)
            .WithTitle($"{templateName} Middle Proposed")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(middle);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(1600, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.BeginPlanningTarget);
    }

    // --- Scenario 8: Leaf-type unknown event ---

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public async Task Validate_LeafUnknownEvent_ReturnsIsValidFalseWithMessage(string templateName)
    {
        var t = GetTemplate(templateName);
        var leaf = new WorkItemBuilder()
            .WithId(1700)
            .WithType(t.LeafType)
            .WithTitle($"{templateName} Task Unknown")
            .WithState(t.ProposedState)
            .Build();
        await SeedAsync(leaf);

        var cmd = CreateCommand(t);
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(1700, "nonexistent_event"));

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
        result.Message.ShouldNotBeNullOrEmpty();
        result.Message!.ShouldContain("nonexistent_event");
    }
}
