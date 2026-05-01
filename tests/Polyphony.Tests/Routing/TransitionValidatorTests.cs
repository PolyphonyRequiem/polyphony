using System.Runtime.CompilerServices;
using Polyphony.Configuration;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Aggregates;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Unit tests for <see cref="TransitionValidator"/>.
/// Covers all 4 precondition types, happy paths, unknown events, and edge cases.
/// </summary>
public sealed class TransitionValidatorTests
{
    private readonly ProcessConfig _config = new ProcessConfigBuilder()
        .WithType("Epic", ["plannable"], new Dictionary<string, string>
        {
            ["begin_planning"] = "Doing",
            ["all_children_complete"] = "Done",
        })
        .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>
        {
            ["begin_planning"] = "Doing",
            ["begin_implementation"] = "Doing",
            ["implementation_complete"] = "Done",
            ["all_children_complete"] = "Done",
        })
        .WithType("Task", ["implementable"], new Dictionary<string, string>
        {
            ["begin_implementation"] = "Doing",
            ["implementation_complete"] = "Done",
        })
        .Build();

    private TransitionValidator CreateValidator() => new(_config);

    // ── Happy path tests ─────────────────────────────────────────────

    [Fact]
    public void Validate_BeginPlanning_WhenProposed_ReturnsValid()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("To Do").Build();

        var result = CreateValidator().Validate(item, "begin_planning", []);

        var valid = AssertValid(result);
        valid.TargetState.ShouldBe("Doing");
        valid.WorkItemId.ShouldBe(1);
        valid.Event.ShouldBe("begin_planning");
    }

    [Fact]
    public void Validate_BeginImplementation_WhenProposed_ReturnsValid()
    {
        var item = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithState("To Do").Build();

        var result = CreateValidator().Validate(item, "begin_implementation", []);

        var valid = AssertValid(result);
        valid.TargetState.ShouldBe("Doing");
    }

    [Fact]
    public void Validate_BeginImplementation_WhenInProgress_ReturnsValid()
    {
        var item = new WorkItemBuilder()
            .WithId(10).WithType("Issue").WithState("Doing").Build();

        var result = CreateValidator().Validate(item, "begin_implementation", []);

        var valid = AssertValid(result);
        valid.TargetState.ShouldBe("Doing");
    }

    [Fact]
    public void Validate_ImplementationComplete_WhenInProgress_ReturnsValid()
    {
        var item = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithState("Doing").Build();

        var result = CreateValidator().Validate(item, "implementation_complete", []);

        var valid = AssertValid(result);
        valid.TargetState.ShouldBe("Done");
    }

    [Fact]
    public void Validate_AllChildrenComplete_WhenAllDone_ReturnsValid()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder()
            .WithId(10).WithType("Issue").WithState("Done").Build();
        var child2 = new WorkItemBuilder()
            .WithId(11).WithType("Issue").WithState("Done").Build();

        var result = CreateValidator().Validate(item, "all_children_complete", [child1, child2]);

        var valid = AssertValid(result);
        valid.TargetState.ShouldBe("Done");
    }

    // ── Unknown event tests ──────────────────────────────────────────

    [Fact]
    public void Validate_UnknownEvent_ReturnsInvalid()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("To Do").Build();

        var result = CreateValidator().Validate(item, "nonexistent_event", []);

        var invalid = AssertInvalid(result);
        invalid.Message.ShouldContain("Unknown event");
        invalid.Message.ShouldContain("nonexistent_event");
    }

    [Fact]
    public void Validate_UnknownType_ReturnsInvalid()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Bug").WithState("New").Build();

        var result = CreateValidator().Validate(item, "begin_planning", []);

        var invalid = AssertInvalid(result);
        invalid.Message.ShouldContain("No transitions defined");
        invalid.Message.ShouldContain("Bug");
    }

    // ── Precondition failure tests ───────────────────────────────────

    [Fact]
    public void Validate_BeginPlanning_WhenInProgress_ReturnsInvalid()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").Build();

        var result = CreateValidator().Validate(item, "begin_planning", []);

        var invalid = AssertInvalid(result);
        invalid.TargetState.ShouldBe("Doing");
        invalid.Message.ShouldContain("begin_planning");
        invalid.Message.ShouldContain("Proposed");
    }

    [Fact]
    public void Validate_BeginImplementation_WhenCompleted_ReturnsInvalid()
    {
        var item = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithState("Done").Build();

        var result = CreateValidator().Validate(item, "begin_implementation", []);

        var invalid = AssertInvalid(result);
        invalid.Message.ShouldContain("begin_implementation");
        invalid.Message.ShouldContain("Proposed or InProgress");
    }

    [Fact]
    public void Validate_ImplementationComplete_WhenProposed_ReturnsInvalid()
    {
        var item = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithState("To Do").Build();

        var result = CreateValidator().Validate(item, "implementation_complete", []);

        var invalid = AssertInvalid(result);
        invalid.Message.ShouldContain("implementation_complete");
        invalid.Message.ShouldContain("InProgress");
    }

    [Fact]
    public void Validate_AllChildrenComplete_WhenChildNotDone_ReturnsInvalid()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder()
            .WithId(10).WithType("Issue").WithState("Done").Build();
        var child2 = new WorkItemBuilder()
            .WithId(11).WithType("Issue").WithState("Doing").Build();

        var result = CreateValidator().Validate(item, "all_children_complete", [child1, child2]);

        var invalid = AssertInvalid(result);
        invalid.Message.ShouldContain("all_children_complete");
        invalid.Message.ShouldContain("child #11");
    }

    [Fact]
    public void Validate_AllChildrenComplete_WhenNoChildren_ReturnsInvalid()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").Build();

        var result = CreateValidator().Validate(item, "all_children_complete", []);

        var invalid = AssertInvalid(result);
        invalid.Message.ShouldContain("no children");
    }

    // ── Edge cases ───────────────────────────────────────────────────

    [Fact]
    public void Validate_EventWithNoPrecondition_ValidIfTransitionExists()
    {
        // Create a config with a custom event that has no precondition rule
        var config = new ProcessConfigBuilder()
            .WithType("Task", ["implementable"], new Dictionary<string, string>
            {
                ["custom_event"] = "CustomState",
            })
            .Build();

        var validator = new TransitionValidator(config);
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Task").WithState("To Do").Build();

        var result = validator.Validate(item, "custom_event", []);

        var valid = AssertValid(result);
        valid.TargetState.ShouldBe("CustomState");
    }

    [Fact]
    public void Validate_AllChildrenComplete_SingleChild_Done_ReturnsValid()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").Build();
        var child = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithState("Done").Build();

        var result = CreateValidator().Validate(item, "all_children_complete", [child]);

        AssertValid(result);
    }

    [Fact]
    public void Validate_AllChildrenComplete_ChildClosed_ReturnsValid()
    {
        // "Closed" maps to Completed category via StateCategoryResolver
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").Build();
        var child = new WorkItemBuilder()
            .WithId(10).WithType("Task").WithState("Closed").Build();

        var result = CreateValidator().Validate(item, "all_children_complete", [child]);

        AssertValid(result);
    }

    [Fact]
    public void Validate_ValidTransition_IncludesTargetStateInMessage()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Task").WithState("Doing").Build();

        var result = CreateValidator().Validate(item, "implementation_complete", []);

        var valid = AssertValid(result);
        valid.Message.ShouldContain("Done");
    }

    [Fact]
    public void Validate_FailedPrecondition_StillIncludesTargetState()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Task").WithState("To Do").Build();

        var result = CreateValidator().Validate(item, "implementation_complete", []);

        var invalid = AssertInvalid(result);
        invalid.TargetState.ShouldBe("Done");
    }

    [Fact]
    public void Validate_AllChildrenComplete_FirstChildNotComplete_FailsFast()
    {
        var item = new WorkItemBuilder()
            .WithId(1).WithType("Epic").WithState("Doing").Build();
        var child1 = new WorkItemBuilder()
            .WithId(10).WithType("Issue").WithState("To Do").Build();
        var child2 = new WorkItemBuilder()
            .WithId(11).WithType("Issue").WithState("Done").Build();

        var result = CreateValidator().Validate(item, "all_children_complete", [child1, child2]);

        var invalid = AssertInvalid(result);
        invalid.Message.ShouldContain("child #10");
    }

    private static ValidTransition AssertValid(TransitionOutcome outcome) =>
        ((IUnion)outcome).Value.ShouldBeOfType<ValidTransition>();

    private static InvalidTransition AssertInvalid(TransitionOutcome outcome) =>
        ((IUnion)outcome).Value.ShouldBeOfType<InvalidTransition>();
}
