using Polyphony.Routing;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Tests for the <see cref="TransitionOutcome"/> discriminated union.
/// Verifies case construction, type identity, nullable contracts, equality, and exhaustive pattern matching.
/// </summary>
public sealed class TransitionOutcomeTests
{
    [Fact]
    public void ValidTransition_ConstructsWithAllProperties()
    {
        var valid = new ValidTransition(42, "begin_planning", "Active", "Transition is valid.");
        valid.WorkItemId.ShouldBe(42);
        valid.Event.ShouldBe("begin_planning");
        valid.TargetState.ShouldBe("Active");
        valid.Message.ShouldBe("Transition is valid.");
    }

    [Fact]
    public void InvalidTransition_ConstructsWithAllProperties()
    {
        var invalid = new InvalidTransition(99, "unknown_event", null, "Unknown event.");
        invalid.WorkItemId.ShouldBe(99);
        invalid.Event.ShouldBe("unknown_event");
        invalid.TargetState.ShouldBeNull();
        invalid.Message.ShouldBe("Unknown event.");
    }

    [Fact]
    public void InvalidTransition_TargetState_CanBeNonNull()
    {
        var invalid = new InvalidTransition(7, "all_children_complete", "Closed", "Precondition failed.");
        invalid.TargetState.ShouldBe("Closed");
    }

    [Fact]
    public void CaseType_ValidTransition_MatchesPattern()
    {
        TransitionOutcome outcome = new ValidTransition(1, "e", "s", "m");
        (outcome is ValidTransition).ShouldBeTrue();
        (outcome is InvalidTransition).ShouldBeFalse();
    }

    [Fact]
    public void CaseType_InvalidTransition_MatchesPattern()
    {
        TransitionOutcome outcome = new InvalidTransition(1, "e", null, "m");
        (outcome is InvalidTransition).ShouldBeTrue();
        (outcome is ValidTransition).ShouldBeFalse();
    }

    [Fact]
    public void PatternMatch_ExhaustiveSwitch_MatchesCorrectCase()
    {
        TransitionOutcome outcome = new ValidTransition(1, "begin_planning", "Active", "ok");

        var matched = outcome switch
        {
            ValidTransition => "valid",
            InvalidTransition => "invalid",
        };

        matched.ShouldBe("valid");
    }

    [Fact]
    public void PatternMatch_InvalidCase_MatchesCorrectly()
    {
        TransitionOutcome outcome = new InvalidTransition(1, "bad", null, "nope");

        var matched = outcome switch
        {
            ValidTransition => "valid",
            InvalidTransition => "invalid",
        };

        matched.ShouldBe("invalid");
    }

    [Theory]
    [MemberData(nameof(AllCasesWithExpectedLabel))]
    public void PatternMatch_AllCases_MatchExpectedLabel(TransitionOutcome outcome, string expectedLabel)
    {
        var label = outcome switch
        {
            ValidTransition => "valid",
            InvalidTransition => "invalid",
            null => throw new ArgumentNullException(nameof(outcome)),
        };

        label.ShouldBe(expectedLabel);
    }

    public static TheoryData<TransitionOutcome, string> AllCasesWithExpectedLabel() => new()
    {
        { new ValidTransition(1, "e", "s", "m"), "valid" },
        { new InvalidTransition(1, "e", null, "m"), "invalid" },
    };

    [Fact]
    public void Equality_SameCase_SameValues_AreEqual()
    {
        var a = new ValidTransition(1, "e", "s", "m");
        var b = new ValidTransition(1, "e", "s", "m");
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_SameCase_DifferentValues_AreNotEqual()
    {
        var a = new ValidTransition(1, "e", "s", "m1");
        var b = new ValidTransition(1, "e", "s", "m2");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentCases_AreNotEqual()
    {
        TransitionOutcome valid = new ValidTransition(1, "e", "s", "m");
        TransitionOutcome invalid = new InvalidTransition(1, "e", "s", "m");
        valid.ShouldNotBe(invalid);
    }

    [Fact]
    public void WithExpression_ValidTransition_CreatesModifiedCopy()
    {
        var original = new ValidTransition(1, "e", "Active", "ok");
        var modified = original with { TargetState = "Closed" };

        modified.TargetState.ShouldBe("Closed");
        original.TargetState.ShouldBe("Active");
        original.ShouldNotBe(modified);
    }

    [Fact]
    public void WithExpression_InvalidTransition_CreatesModifiedCopy()
    {
        var original = new InvalidTransition(1, "e", null, "bad");
        var modified = original with { TargetState = "Active", Message = "updated" };

        modified.TargetState.ShouldBe("Active");
        modified.Message.ShouldBe("updated");
        original.TargetState.ShouldBeNull();
        original.Message.ShouldBe("bad");
    }

    [Theory]
    [MemberData(nameof(AllCasesWithProperties))]
    public void Properties_AreAccessible_OnAllCases(
        TransitionOutcome outcome,
        int expectedId,
        string expectedEvent,
        string? expectedTarget,
        string expectedMessage)
    {
        var (id, evt, target, message) = outcome switch
        {
            ValidTransition v => (v.WorkItemId, v.Event, (string?)v.TargetState, v.Message),
            InvalidTransition iv => (iv.WorkItemId, iv.Event, iv.TargetState, iv.Message),
            null => throw new ArgumentNullException(nameof(outcome)),
        };

        id.ShouldBe(expectedId);
        evt.ShouldBe(expectedEvent);
        target.ShouldBe(expectedTarget);
        message.ShouldBe(expectedMessage);
    }

    public static TheoryData<TransitionOutcome, int, string, string?, string> AllCasesWithProperties() => new()
    {
        { new ValidTransition(42, "begin_planning", "Active", "valid msg"), 42, "begin_planning", "Active", "valid msg" },
        { new InvalidTransition(99, "unknown", null, "invalid msg"), 99, "unknown", null, "invalid msg" },
        { new InvalidTransition(7, "all_children_complete", "Closed", "precondition failed"), 7, "all_children_complete", "Closed", "precondition failed" },
    };
}
