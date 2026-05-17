using Polyphony.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public class MagicCommentDetectorTests
{
    [Fact]
    public void Count_NoBodies_IsZero()
    {
        MagicCommentDetector.Count(Array.Empty<string?>()).ShouldBe(0);
    }

    [Fact]
    public void Count_BodiesWithNoMatches_IsZero()
    {
        MagicCommentDetector.Count(new[] { "LGTM", "ship it", "👍", null, string.Empty })
            .ShouldBe(0);
    }

    [Fact]
    public void Count_BareApproveAtLineStart_Matches()
    {
        MagicCommentDetector.Count(new[] { "polyphony:approve" }).ShouldBe(1);
    }

    [Fact]
    public void Count_ShaBoundApprove_StillMatches_BothFormsCounted()
    {
        // The detector is form-agnostic — anything matching the magic
        // prefix counts as a stale workaround. We don't differentiate
        // SHA-bound from bare-form since the routing no longer cares.
        MagicCommentDetector.Count(new[] { "polyphony:approve abc1234 LGTM" }).ShouldBe(1);
    }

    [Fact]
    public void Count_RequestChanges_Matches()
    {
        MagicCommentDetector.Count(new[] { "polyphony:request-changes deadbeef" }).ShouldBe(1);
    }

    [Fact]
    public void Count_CaseInsensitive()
    {
        MagicCommentDetector.Count(new[] { "POLYPHONY:APPROVE", "Polyphony:Request-Changes abc" })
            .ShouldBe(2);
    }

    [Fact]
    public void Count_MidLineMatch_Ignored()
    {
        // Anchored to line-start so narrative discussion about the
        // commands doesn't trigger a false positive.
        MagicCommentDetector.Count(new[] { "see also polyphony:approve" }).ShouldBe(0);
    }

    [Fact]
    public void Count_MultipleAcrossBodies_Sums()
    {
        // Regex is line-anchored (^\s*polyphony:...); a marker mid-line
        // does NOT count. The 2nd line of the 3rd body starts with
        // "and another" so it is ignored.
        MagicCommentDetector.Count(new[]
        {
            "polyphony:approve",
            "polyphony:request-changes abc",
            "polyphony:approve def\nand another polyphony:approve",
        }).ShouldBe(3);
    }

    [Fact]
    public void FormatWarning_SingularPhrasing_ForCountOne()
    {
        MagicCommentDetector.FormatWarning(1)
            .ShouldContain("Detected 1 stale magic comment.");
    }

    [Fact]
    public void FormatWarning_PluralPhrasing_ForCountAboveOne()
    {
        MagicCommentDetector.FormatWarning(5)
            .ShouldContain("Detected 5 stale magic comments.");
    }

    [Fact]
    public void FormatWarning_AlwaysExplainsRetirement()
    {
        var warning = MagicCommentDetector.FormatWarning(2);
        warning.ShouldContain("no longer honored");
        warning.ShouldContain("platform UI");
    }
}
