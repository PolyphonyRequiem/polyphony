using Polyphony.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public class PrCommentMarkerTests
{
    [Fact]
    public void TryParse_NullBody_ReturnsNull()
    {
        PrCommentMarker.TryParse(null).ShouldBeNull();
    }

    [Fact]
    public void TryParse_EmptyBody_ReturnsNull()
    {
        PrCommentMarker.TryParse(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void TryParse_NoMarker_ReturnsNull()
    {
        PrCommentMarker.TryParse("LGTM, ship it").ShouldBeNull();
    }

    [Fact]
    public void TryParse_CanonicalForm_AllAttributesParsed()
    {
        var marker = PrCommentMarker.TryParse(
            "<!-- polyphony:agent-comment agent=plan_reviewer head_sha=abc1234 run_id=xyz -->\n\nBody here");
        marker.ShouldNotBeNull();
        marker!.Agent.ShouldBe("plan_reviewer");
        marker.HeadSha.ShouldBe("abc1234");
        marker.RunId.ShouldBe("xyz");
    }

    [Fact]
    public void TryParse_AgentOnly_OptionalFieldsAreNull()
    {
        var marker = PrCommentMarker.TryParse(
            "<!-- polyphony:agent-comment agent=feature_pr_reviewer -->");
        marker.ShouldNotBeNull();
        marker!.Agent.ShouldBe("feature_pr_reviewer");
        marker.HeadSha.ShouldBeNull();
        marker.RunId.ShouldBeNull();
    }

    [Fact]
    public void TryParse_LeadingWhitespace_StillRecognized()
    {
        var marker = PrCommentMarker.TryParse(
            "   \t<!-- polyphony:agent-comment agent=plan_reviewer -->\nbody");
        marker.ShouldNotBeNull();
        marker!.Agent.ShouldBe("plan_reviewer");
    }

    [Fact]
    public void TryParse_MarkerNotAtStart_NotRecognized()
    {
        // Marker must be at the very start; mid-comment markers are
        // ignored (no false positives from a reviewer pasting an
        // example into a normal comment body).
        var marker = PrCommentMarker.TryParse(
            "Look at this comment from us:\n<!-- polyphony:agent-comment agent=plan_reviewer -->");
        marker.ShouldBeNull();
    }

    [Fact]
    public void TryParse_MissingAgentAttribute_ReturnsNull()
    {
        var marker = PrCommentMarker.TryParse(
            "<!-- polyphony:agent-comment head_sha=abc1234 -->");
        marker.ShouldBeNull();
    }

    [Fact]
    public void TryParse_AttributeOrderIndependent()
    {
        var marker = PrCommentMarker.TryParse(
            "<!-- polyphony:agent-comment run_id=z head_sha=a agent=plan_reviewer -->");
        marker.ShouldNotBeNull();
        marker!.Agent.ShouldBe("plan_reviewer");
        marker.HeadSha.ShouldBe("a");
        marker.RunId.ShouldBe("z");
    }

    [Fact]
    public void Format_AgentOnly_OmitsOptionalAttributes()
    {
        PrCommentMarker.Format("plan_reviewer")
            .ShouldBe("<!-- polyphony:agent-comment agent=plan_reviewer -->");
    }

    [Fact]
    public void Format_WithSha_IncludesSha()
    {
        PrCommentMarker.Format("plan_reviewer", headSha: "abc1234")
            .ShouldBe("<!-- polyphony:agent-comment agent=plan_reviewer head_sha=abc1234 -->");
    }

    [Fact]
    public void Format_WithAllFields_IncludesAll()
    {
        PrCommentMarker.Format("plan_reviewer", headSha: "abc1234", runId: "xyz")
            .ShouldBe("<!-- polyphony:agent-comment agent=plan_reviewer head_sha=abc1234 run_id=xyz -->");
    }

    [Fact]
    public void Format_EmptyAgent_Throws()
    {
        Should.Throw<ArgumentException>(() => PrCommentMarker.Format(string.Empty));
    }

    [Fact]
    public void Format_RoundTripsThroughTryParse()
    {
        var rendered = PrCommentMarker.Format("plan_reviewer", "abc1234", "run-42-uuid-style");
        var parsed = PrCommentMarker.TryParse(rendered + "\n\nbody");
        parsed.ShouldNotBeNull();
        parsed!.Agent.ShouldBe("plan_reviewer");
        parsed.HeadSha.ShouldBe("abc1234");
        parsed.RunId.ShouldBe("run-42-uuid-style");
    }
}
