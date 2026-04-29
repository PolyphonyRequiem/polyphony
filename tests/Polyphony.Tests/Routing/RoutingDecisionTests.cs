using Polyphony.Routing;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Tests for <see cref="RoutingDecision"/> ensuring the record is properly constructible
/// and usable in unit tests with init-based properties.
/// </summary>
public sealed class RoutingDecisionTests
{
    [Fact]
    public void Create_WithRequiredProperties_SetsValues()
    {
        var decision = new RoutingDecision
        {
            Phase = SdlcPhase.NeedsPlanning,
            Action = SdlcAction.Plan
        };

        decision.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
        decision.Action.ShouldBe(SdlcAction.Plan);
        decision.Message.ShouldBeNull();
    }

    [Fact]
    public void Create_WithMessage_SetsAllProperties()
    {
        var decision = new RoutingDecision
        {
            Phase = SdlcPhase.InProgress,
            Action = SdlcAction.Monitor,
            Message = "3 of 5 children completed"
        };

        decision.Phase.ShouldBe(SdlcPhase.InProgress);
        decision.Action.ShouldBe(SdlcAction.Monitor);
        decision.Message.ShouldBe("3 of 5 children completed");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new RoutingDecision
        {
            Phase = SdlcPhase.Done,
            Action = SdlcAction.Close,
            Message = "all done"
        };

        var b = new RoutingDecision
        {
            Phase = SdlcPhase.Done,
            Action = SdlcAction.Close,
            Message = "all done"
        };

        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentPhase_AreNotEqual()
    {
        var a = new RoutingDecision { Phase = SdlcPhase.Done, Action = SdlcAction.Close };
        var b = new RoutingDecision { Phase = SdlcPhase.InProgress, Action = SdlcAction.Close };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentAction_AreNotEqual()
    {
        var a = new RoutingDecision { Phase = SdlcPhase.Done, Action = SdlcAction.Close };
        var b = new RoutingDecision { Phase = SdlcPhase.Done, Action = SdlcAction.None };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentMessage_AreNotEqual()
    {
        var a = new RoutingDecision { Phase = SdlcPhase.Done, Action = SdlcAction.Close, Message = "a" };
        var b = new RoutingDecision { Phase = SdlcPhase.Done, Action = SdlcAction.Close, Message = "b" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void With_CreatesModifiedCopy()
    {
        var original = new RoutingDecision
        {
            Phase = SdlcPhase.NeedsPlanning,
            Action = SdlcAction.Plan
        };

        var modified = original with { Phase = SdlcPhase.NeedsSeeding, Action = SdlcAction.Seed };

        modified.Phase.ShouldBe(SdlcPhase.NeedsSeeding);
        modified.Action.ShouldBe(SdlcAction.Seed);
        original.Phase.ShouldBe(SdlcPhase.NeedsPlanning);
    }

    [Fact]
    public void ToString_ContainsPropertyValues()
    {
        var decision = new RoutingDecision
        {
            Phase = SdlcPhase.Unknown,
            Action = SdlcAction.None,
            Message = "test"
        };

        var str = decision.ToString();
        str.ShouldContain(SdlcPhase.Unknown);
        str.ShouldContain(SdlcAction.None);
    }
}
