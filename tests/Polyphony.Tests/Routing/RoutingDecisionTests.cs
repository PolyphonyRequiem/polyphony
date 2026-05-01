using Polyphony.Routing;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Tests for the <see cref="RoutingDecision"/> discriminated union.
/// Verifies case construction, type identity, equality, and exhaustive pattern matching.
/// </summary>
public sealed class RoutingDecisionTests
{
    [Fact]
    public void NeedsPlanning_ConstructsWithMessage()
    {
        var decision = new NeedsPlanning("Item needs planning.");
        decision.Message.ShouldBe("Item needs planning.");
    }

    [Fact]
    public void NeedsSeeding_ConstructsWithMessage()
    {
        var decision = new NeedsSeeding("Needs decomposition.");
        decision.Message.ShouldBe("Needs decomposition.");
    }

    [Fact]
    public void ReadyForImplementation_ConstructsWithMessage()
    {
        var decision = new ReadyForImplementation("Ready to implement.");
        decision.Message.ShouldBe("Ready to implement.");
    }

    [Fact]
    public void ImplementationInProgress_ConstructsWithMessage()
    {
        var decision = new ImplementationInProgress("3 of 5 children completed");
        decision.Message.ShouldBe("3 of 5 children completed");
    }

    [Fact]
    public void ReadyForCompletion_ConstructsWithMessage()
    {
        var decision = new ReadyForCompletion("All children done.");
        decision.Message.ShouldBe("All children done.");
    }

    [Fact]
    public void RoutingDone_ConstructsWithMessage()
    {
        var decision = new RoutingDone("Complete.");
        decision.Message.ShouldBe("Complete.");
    }

    [Fact]
    public void RoutingRemoved_ConstructsWithMessage()
    {
        var decision = new RoutingRemoved("Item removed.");
        decision.Message.ShouldBe("Item removed.");
    }

    [Fact]
    public void RoutingUnknown_ConstructsWithMessage()
    {
        var decision = new RoutingUnknown("Unrecognized state.");
        decision.Message.ShouldBe("Unrecognized state.");
    }

    [Fact]
    public void CaseType_NeedsPlanning_MatchesPattern()
    {
        RoutingDecision rd = new NeedsPlanning("test");
        (rd is NeedsPlanning).ShouldBeTrue();
    }

    [Fact]
    public void CaseType_NeedsSeeding_MatchesPattern()
    {
        RoutingDecision rd = new NeedsSeeding("test");
        (rd is NeedsSeeding).ShouldBeTrue();
    }

    [Fact]
    public void CaseType_ReadyForImplementation_MatchesPattern()
    {
        RoutingDecision rd = new ReadyForImplementation("test");
        (rd is ReadyForImplementation).ShouldBeTrue();
    }

    [Fact]
    public void CaseType_ImplementationInProgress_MatchesPattern()
    {
        RoutingDecision rd = new ImplementationInProgress("test");
        (rd is ImplementationInProgress).ShouldBeTrue();
    }

    [Fact]
    public void CaseType_ReadyForCompletion_MatchesPattern()
    {
        RoutingDecision rd = new ReadyForCompletion("test");
        (rd is ReadyForCompletion).ShouldBeTrue();
    }

    [Fact]
    public void CaseType_RoutingDone_MatchesPattern()
    {
        RoutingDecision rd = new RoutingDone("test");
        (rd is RoutingDone).ShouldBeTrue();
    }

    [Fact]
    public void CaseType_RoutingRemoved_MatchesPattern()
    {
        RoutingDecision rd = new RoutingRemoved("test");
        (rd is RoutingRemoved).ShouldBeTrue();
    }

    [Fact]
    public void CaseType_RoutingUnknown_MatchesPattern()
    {
        RoutingDecision rd = new RoutingUnknown("test");
        (rd is RoutingUnknown).ShouldBeTrue();
    }

    [Fact]
    public void Equality_SameCase_SameMessage_AreEqual()
    {
        var a = new RoutingDone("done");
        var b = new RoutingDone("done");

        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_SameCase_DifferentMessage_AreNotEqual()
    {
        var a = new RoutingDone("a");
        var b = new RoutingDone("b");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentCases_SameMessage_AreNotEqual()
    {
        RoutingDecision x = new NeedsPlanning("x");
        RoutingDecision y = new NeedsSeeding("x");

        x.ShouldNotBe(y);
    }

    [Theory]
    [MemberData(nameof(AllCasePairs))]
    public void Equality_DistinctCases_AreNeverEqual(RoutingDecision left, RoutingDecision right)
    {
        left.ShouldNotBe(right);
    }

    public static TheoryData<RoutingDecision, RoutingDecision> AllCasePairs()
    {
        var cases = AllCases("same");
        var data = new TheoryData<RoutingDecision, RoutingDecision>();
        for (var i = 0; i < cases.Length; i++)
        {
            for (var j = i + 1; j < cases.Length; j++)
            {
                data.Add(cases[i], cases[j]);
            }
        }
        return data;
    }

    [Fact]
    public void PatternMatch_ExhaustiveSwitch_MatchesCorrectCase()
    {
        RoutingDecision d = new ReadyForCompletion("ready");

        var matched = d switch
        {
            NeedsPlanning => "plan",
            NeedsSeeding => "seed",
            ReadyForImplementation => "impl",
            ImplementationInProgress => "progress",
            ReadyForCompletion => "complete",
            RoutingDone => "done",
            RoutingRemoved => "removed",
            RoutingUnknown => "unknown",
        };

        matched.ShouldBe("complete");
    }

    [Theory]
    [MemberData(nameof(AllCasesWithExpectedLabel))]
    public void PatternMatch_AllCases_MatchExpectedLabel(RoutingDecision decision, string expectedLabel)
    {
        var label = decision switch
        {
            NeedsPlanning => "plan",
            NeedsSeeding => "seed",
            ReadyForImplementation => "impl",
            ImplementationInProgress => "progress",
            ReadyForCompletion => "complete",
            RoutingDone => "done",
            RoutingRemoved => "removed",
            RoutingUnknown => "unknown",
            null => throw new ArgumentNullException(nameof(decision)),
        };

        label.ShouldBe(expectedLabel);
    }

    public static TheoryData<RoutingDecision, string> AllCasesWithExpectedLabel() => new()
    {
        { new NeedsPlanning("msg"), "plan" },
        { new NeedsSeeding("msg"), "seed" },
        { new ReadyForImplementation("msg"), "impl" },
        { new ImplementationInProgress("msg"), "progress" },
        { new ReadyForCompletion("msg"), "complete" },
        { new RoutingDone("msg"), "done" },
        { new RoutingRemoved("msg"), "removed" },
        { new RoutingUnknown("msg"), "unknown" },
    };

    [Theory]
    [MemberData(nameof(AllCasesWithMessage))]
    public void Message_IsAccessible_OnAllCases(RoutingDecision decision, string expectedMessage)
    {
        // Access Message via pattern matching since the union base doesn't expose it directly
        var message = decision switch
        {
            NeedsPlanning d => d.Message,
            NeedsSeeding d => d.Message,
            ReadyForImplementation d => d.Message,
            ImplementationInProgress d => d.Message,
            ReadyForCompletion d => d.Message,
            RoutingDone d => d.Message,
            RoutingRemoved d => d.Message,
            RoutingUnknown d => d.Message,
            null => throw new ArgumentNullException(nameof(decision)),
        };

        message.ShouldBe(expectedMessage);
    }

    public static TheoryData<RoutingDecision, string> AllCasesWithMessage() => new()
    {
        { new NeedsPlanning("plan-msg"), "plan-msg" },
        { new NeedsSeeding("seed-msg"), "seed-msg" },
        { new ReadyForImplementation("impl-msg"), "impl-msg" },
        { new ImplementationInProgress("progress-msg"), "progress-msg" },
        { new ReadyForCompletion("complete-msg"), "complete-msg" },
        { new RoutingDone("done-msg"), "done-msg" },
        { new RoutingRemoved("removed-msg"), "removed-msg" },
        { new RoutingUnknown("unknown-msg"), "unknown-msg" },
    };

    [Fact]
    public void WithExpression_CreatesModifiedCopy()
    {
        var original = new NeedsPlanning("original");
        var modified = original with { Message = "modified" };

        modified.Message.ShouldBe("modified");
        original.Message.ShouldBe("original");
        original.ShouldNotBe(modified);
    }

    private static RoutingDecision[] AllCases(string message) =>
    [
        new NeedsPlanning(message),
        new NeedsSeeding(message),
        new ReadyForImplementation(message),
        new ImplementationInProgress(message),
        new ReadyForCompletion(message),
        new RoutingDone(message),
        new RoutingRemoved(message),
        new RoutingUnknown(message),
    ];
}
