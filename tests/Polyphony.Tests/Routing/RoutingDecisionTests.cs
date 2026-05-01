using Polyphony.Routing;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Tests for <see cref="RoutingDecision"/> DU ensuring each case record is properly
/// constructible and maps correctly through <see cref="RoutingDecisionMapper"/>.
/// </summary>
public sealed class RoutingDecisionTests
{
    [Fact]
    public void NeedsPlanning_MapsCorrectly()
    {
        RoutingDecision decision = new NeedsPlanning("plan it");
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        phase.ShouldBe(SdlcPhase.NeedsPlanning);
        action.ShouldBe(SdlcAction.Plan);
        message.ShouldBe("plan it");
    }

    [Fact]
    public void NeedsSeeding_MapsCorrectly()
    {
        RoutingDecision decision = new NeedsSeeding("seed it");
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        phase.ShouldBe(SdlcPhase.NeedsSeeding);
        action.ShouldBe(SdlcAction.Seed);
        message.ShouldBe("seed it");
    }

    [Fact]
    public void ReadyForImplementation_MapsCorrectly()
    {
        RoutingDecision decision = new ReadyForImplementation("implement it");
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        phase.ShouldBe(SdlcPhase.ReadyForImplementation);
        action.ShouldBe(SdlcAction.Implement);
        message.ShouldBe("implement it");
    }

    [Fact]
    public void ImplementationInProgress_MapsCorrectly()
    {
        RoutingDecision decision = new ImplementationInProgress("3 of 5 children completed");
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        phase.ShouldBe(SdlcPhase.InProgress);
        action.ShouldBe(SdlcAction.Monitor);
        message.ShouldBe("3 of 5 children completed");
    }

    [Fact]
    public void ReadyForCompletion_MapsCorrectly()
    {
        RoutingDecision decision = new ReadyForCompletion("close it");
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        phase.ShouldBe(SdlcPhase.ReadyForCompletion);
        action.ShouldBe(SdlcAction.Close);
        message.ShouldBe("close it");
    }

    [Fact]
    public void RoutingDone_MapsCorrectly()
    {
        RoutingDecision decision = new RoutingDone("all done");
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        phase.ShouldBe(SdlcPhase.Done);
        action.ShouldBe(SdlcAction.None);
        message.ShouldBe("all done");
    }

    [Fact]
    public void RoutingRemoved_MapsCorrectly()
    {
        RoutingDecision decision = new RoutingRemoved("removed");
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        phase.ShouldBe(SdlcPhase.Removed);
        action.ShouldBe(SdlcAction.None);
        message.ShouldBe("removed");
    }

    [Fact]
    public void RoutingUnknown_MapsCorrectly()
    {
        RoutingDecision decision = new RoutingUnknown("test");
        var (phase, action, message) = RoutingDecisionMapper.ToComponents(decision);

        phase.ShouldBe(SdlcPhase.Unknown);
        action.ShouldBe(SdlcAction.None);
        message.ShouldBe("test");
    }

    [Fact]
    public void Equality_SameCase_SameMessage_AreEqual()
    {
        var a = new RoutingDone("all done");
        var b = new RoutingDone("all done");

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
    public void Equality_DifferentCases_AreNotEqual()
    {
        RoutingDecision a = new RoutingDone("done");
        RoutingDecision b = new RoutingRemoved("done");

        a.ShouldNotBe(b);
    }
}
