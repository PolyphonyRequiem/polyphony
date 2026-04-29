using System.Text.Json;
using Polyphony.Commands;
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
    [Fact]
    public void Validate_ReturnsSuccessExitCode()
    {
        var cmd = new ValidateCommand();
        var (exitCode, _) = CaptureConsole(() => cmd.Validate(100, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public void Validate_OutputDeserializesToValidateResult()
    {
        var cmd = new ValidateCommand();
        var (_, output) = CaptureConsole(() => cmd.Validate(100, "begin_planning"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
    }

    [Fact]
    public void Validate_OutputContainsWorkItemIdAndEvent()
    {
        var cmd = new ValidateCommand();
        var (_, output) = CaptureConsole(() => cmd.Validate(42, "implementation_complete"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(42);
        result.Event.ShouldBe("implementation_complete");
    }

    [Fact]
    public async Task Validate_ValidEvent_ReturnsValidJson()
    {
        // Seed an Epic in "To Do" — begin_planning is a valid event for this type
        var epic = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Test Epic")
            .WithState("To Do")
            .Build();
        await SeedAsync(epic);

        var cmd = new ValidateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(100, "begin_planning"));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(100);
        result.Event.ShouldBe("begin_planning");
    }

    [Fact]
    public async Task Validate_InvalidEvent_ReturnsIsValidFalse()
    {
        // Seed a Task — "nonexistent_event" is not a valid event
        var task = new WorkItemBuilder()
            .WithId(200)
            .WithType("Task")
            .WithTitle("Test Task")
            .WithState("To Do")
            .Build();
        await SeedAsync(task);

        var cmd = new ValidateCommand();
        var (_, output) = CaptureConsole(() => cmd.Validate(200, "nonexistent_event"));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ValidateResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validate_OutputContainsIsValidField()
    {
        var cmd = new ValidateCommand();
        var (_, output) = CaptureConsole(() => cmd.Validate(100, "begin_planning"));

        // Verify the JSON contains the is_valid field (snake_case per PolyphonyJsonContext)
        output.ShouldContain("\"is_valid\"");
    }

    [Fact]
    public async Task Validate_SeededDatabase_WorkItemCanBeQueried()
    {
        var issue = new WorkItemBuilder()
            .WithId(300)
            .WithType("Issue")
            .WithTitle("Validation Target")
            .WithState("Doing")
            .Build();
        await SeedAsync(issue);

        var loaded = await Repository.GetByIdAsync(300);
        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(300);
        loaded.State.ShouldBe("Doing");
    }
}
