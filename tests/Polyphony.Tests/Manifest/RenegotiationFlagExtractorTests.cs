using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Pure-logic tests for <see cref="RenegotiationFlagExtractor"/>. The
/// verb adapter (<c>polyphony plan extract-renegotiation-flag</c>) is
/// covered separately in
/// <see cref="Polyphony.Tests.Commands.PlanCommandsExtractRenegotiationFlagTests"/>.
/// </summary>
public sealed class RenegotiationFlagExtractorTests
{
    private const string Open = RenegotiationFlagExtractor.OpenTag;
    private const string Close = RenegotiationFlagExtractor.CloseTag;

    [Fact]
    public void NullBody_Absent()
    {
        var result = RenegotiationFlagExtractor.Extract(null);
        result.Status.ShouldBe(RenegotiationFlagExtractor.ExtractStatus.Absent);
        result.FlagPresent.ShouldBeFalse();
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void EmptyBody_Absent()
    {
        var result = RenegotiationFlagExtractor.Extract("");
        result.Status.ShouldBe(RenegotiationFlagExtractor.ExtractStatus.Absent);
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void NoFence_Absent()
    {
        var result = RenegotiationFlagExtractor.Extract("Just a regular plan PR body.");
        result.Status.ShouldBe(RenegotiationFlagExtractor.ExtractStatus.Absent);
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void WellFormedSingleBlock_PresentWithReason()
    {
        var body = $"{Open}\nReason here.\n{Close}";
        var result = RenegotiationFlagExtractor.Extract(body);
        result.Status.ShouldBe(RenegotiationFlagExtractor.ExtractStatus.Present);
        result.FlagPresent.ShouldBeTrue();
        result.Reason.ShouldBe("Reason here.");
    }

    [Fact]
    public void TwoBlocks_ConcatenatedWithSingleBlankLine()
    {
        var body = $"{Open}\nA\n{Close}\n\n{Open}\nB\n{Close}";
        var result = RenegotiationFlagExtractor.Extract(body);
        result.Reason.ShouldBe("A\n\nB");
    }

    [Fact]
    public void WhitespaceOnlyBlock_PresentWithEmptyReason()
    {
        var body = $"{Open}\n   \n\t\n{Close}";
        var result = RenegotiationFlagExtractor.Extract(body);
        result.FlagPresent.ShouldBeTrue();
        result.Reason.ShouldBe(string.Empty);
    }

    [Fact]
    public void OpeningWithoutClosing_Malformed()
    {
        var body = $"{Open}\nopen never closes";
        var result = RenegotiationFlagExtractor.Extract(body);
        result.Status.ShouldBe(RenegotiationFlagExtractor.ExtractStatus.Malformed);
        result.FlagPresent.ShouldBeFalse();
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void OneClosedAndOneUnclosed_Malformed()
    {
        var body = $"{Open}\nclosed reason\n{Close}\nthen later: {Open}\nunclosed";
        var result = RenegotiationFlagExtractor.Extract(body);
        result.Status.ShouldBe(RenegotiationFlagExtractor.ExtractStatus.Malformed);
        result.FlagPresent.ShouldBeFalse();
    }

    [Fact]
    public void InnerWhitespacePreserved_OuterTrimmed()
    {
        var body = $"{Open}\n   line one\n   line two   \n{Close}";
        var result = RenegotiationFlagExtractor.Extract(body);
        result.Reason.ShouldBe("line one\n   line two");
    }

    [Fact]
    public void TolerantToInnerWhitespaceInTags()
    {
        // The extractor allows internal whitespace inside the comment to
        // be tolerant of editors that insert padding.
        var body = "<!-- polyphony:requests-parent-change   -->\nx\n<!--   /polyphony:requests-parent-change -->";
        var result = RenegotiationFlagExtractor.Extract(body);
        result.FlagPresent.ShouldBeTrue();
        result.Reason.ShouldBe("x");
    }
}
