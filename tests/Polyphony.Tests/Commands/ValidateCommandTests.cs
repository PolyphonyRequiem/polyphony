using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <see cref="ValidateCommand"/> using an in-memory SQLite database.
/// Tests verify exit codes, JSON output shape, and event/work-item fields.
/// The database is seeded with scenarios matching the coverage matrix.
/// </summary>
public sealed class ValidateCommandTests : CommandTestBase
{
    private ValidateCommand CreateCommand() => new(new TransitionValidator(Config), Repository);

    [Fact]
    public async Task Validate_WorkItemNotFound_ReturnsCacheErrorExitCode()
    {
        var cmd = CreateCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Validate(999, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.CacheError);
    }

    [Fact]
    public async Task Validate_WorkItemNotFound_OutputsErrorJson()
    {
        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(999, "begin_planning"));

        output.ShouldContain("\"error\"");
        output.ShouldContain("\"work_item_id\":999");
    }

    [Fact]
    public async Task Validate_ValidEvent_ReturnsSuccessExitCode()
    {
        var epic = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Test Epic")
            .WithState("To Do")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Validate(100, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public async Task Validate_ValidEvent_ReturnsIsValidTrueWithTargetState()
    {
        var epic = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Test Epic")
            .WithState("To Do")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(100, "begin_planning"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(100);
        result.Event.ShouldBe("begin_planning");
        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe("Doing");
    }

    [Fact]
    public async Task Validate_UnknownEvent_ReturnsRoutingFailureExitCode()
    {
        var task = new WorkItemBuilder()
            .WithId(200)
            .WithType("Task")
            .WithTitle("Test Task")
            .WithState("To Do")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Validate(200, "nonexistent_event"));

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task Validate_UnknownEvent_ReturnsIsValidFalseWithMessage()
    {
        var task = new WorkItemBuilder()
            .WithId(200)
            .WithType("Task")
            .WithTitle("Test Task")
            .WithState("To Do")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(200, "nonexistent_event"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
        result.Message.ShouldNotBeNullOrEmpty();
        result.Message.ShouldContain("nonexistent_event");
    }

    [Fact]
    public async Task Validate_InvalidPrecondition_ReturnsRoutingFailureExitCode()
    {
        // Epic in "Doing" (InProgress) — begin_planning requires Proposed
        var epic = new WorkItemBuilder()
            .WithId(101)
            .WithType("Epic")
            .WithTitle("In Progress Epic")
            .WithState("Doing")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.Validate(101, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task Validate_InvalidPrecondition_ReturnsIsValidFalseWithMessage()
    {
        var epic = new WorkItemBuilder()
            .WithId(101)
            .WithType("Epic")
            .WithTitle("In Progress Epic")
            .WithState("Doing")
            .Build();
        await SeedAsync(epic);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(101, "begin_planning"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
        result.TargetState.ShouldBe("Doing");
        result.Message!.ShouldContain("begin_planning");
    }

    [Fact]
    public async Task Validate_OutputContainsIsValidField()
    {
        var task = new WorkItemBuilder()
            .WithId(300)
            .WithType("Task")
            .WithTitle("Snake Case Check")
            .WithState("To Do")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(300, "begin_implementation"));

        output.ShouldContain("\"is_valid\"");
    }

    [Fact]
    public async Task Validate_OutputUsesSnakeCasePropertyNames()
    {
        var task = new WorkItemBuilder()
            .WithId(400)
            .WithType("Task")
            .WithTitle("Output Format")
            .WithState("To Do")
            .Build();
        await SeedAsync(task);

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Validate(400, "begin_implementation"));

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"is_valid\"");
        output.ShouldContain("\"target_state\"");
    }

    [Fact]
    public async Task Validate_SeededDatabase_WorkItemCanBeQueried()
    {
        var issue = new WorkItemBuilder()
            .WithId(500)
            .WithType("Issue")
            .WithTitle("Validation Target")
            .WithState("Doing")
            .Build();
        await SeedAsync(issue);

        var loaded = await Repository.GetByIdAsync(500);
        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(500);
        loaded.State.ShouldBe("Doing");
    }

    [Fact]
    public async Task Validate_AllChildrenComplete_EventIsValid()
    {
        var (epic, children) = new WorkItemBuilder()
            .WithId(600)
            .WithType("Epic")
            .WithTitle("Completed Children Epic")
            .WithState("Doing")
            .WithChildren(
                new WorkItemBuilder().WithId(601).WithType("Task").WithTitle("Task 1").WithState("Done"),
                new WorkItemBuilder().WithId(602).WithType("Task").WithTitle("Task 2").WithState("Done"))
            .BuildAll();
        await SeedAsync([epic, .. children]);

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Validate(600, "implementation_complete"));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe("Done");
    }
}
