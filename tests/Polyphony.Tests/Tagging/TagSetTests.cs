using Polyphony.Tagging;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Tagging;

/// <summary>
/// Pure unit tests for <see cref="TagSet"/> — no DI, no I/O. Covers parsing,
/// case-insensitive comparison, idempotent mutations (referential equality
/// signal), and the <c>"; "</c> formatting contract.
/// </summary>
public sealed class TagSetTests
{
    // ─── Parse ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  ")]
    public void Parse_NullOrWhitespace_ReturnsEmpty(string? raw)
    {
        var s = TagSet.Parse(raw);
        s.Count.ShouldBe(0);
        s.ShouldBeSameAs(TagSet.Empty);
    }

    [Fact]
    public void Parse_SingleTag_ReturnsSingleton()
    {
        var s = TagSet.Parse("polyphony");
        s.Count.ShouldBe(1);
        s.Contains("polyphony").ShouldBeTrue();
    }

    [Fact]
    public void Parse_SemicolonDelimited_ReturnsAllTokens()
    {
        var s = TagSet.Parse("polyphony; polyphony:root; twig");
        s.Count.ShouldBe(3);
        s.ToArray().ShouldBe(["polyphony", "polyphony:root", "twig"]);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundTokens()
    {
        var s = TagSet.Parse("  polyphony  ;   twig   ");
        s.ToArray().ShouldBe(["polyphony", "twig"]);
    }

    [Fact]
    public void Parse_SkipsBlankTokens()
    {
        var s = TagSet.Parse("polyphony;;;twig;");
        s.ToArray().ShouldBe(["polyphony", "twig"]);
    }

    [Fact]
    public void Parse_FoldsCaseInsensitiveDuplicates_PreservingFirstSeenCasing()
    {
        var s = TagSet.Parse("Polyphony; polyphony; POLYPHONY");
        s.Count.ShouldBe(1);
        s.ToArray().ShouldBe(["Polyphony"]);
    }

    // ─── Contains ───────────────────────────────────────────────────────────

    [Fact]
    public void Contains_IsCaseInsensitive()
    {
        var s = TagSet.Parse("Polyphony");
        s.Contains("polyphony").ShouldBeTrue();
        s.Contains("POLYPHONY").ShouldBeTrue();
        s.Contains("polyphony:root").ShouldBeFalse();
    }

    // ─── Add ────────────────────────────────────────────────────────────────

    [Fact]
    public void Add_NewTag_ReturnsNewInstance_PreservesOrder()
    {
        var before = TagSet.Parse("twig");
        var after = before.Add("polyphony");

        ReferenceEquals(before, after).ShouldBeFalse();
        after.ToArray().ShouldBe(["twig", "polyphony"]);
    }

    [Fact]
    public void Add_DuplicateTag_ReturnsSameInstance_NoOpSignal()
    {
        var before = TagSet.Parse("polyphony; twig");
        var after = before.Add("polyphony");

        ReferenceEquals(before, after).ShouldBeTrue();
    }

    [Fact]
    public void Add_DuplicateCaseDifference_ReturnsSameInstance()
    {
        var before = TagSet.Parse("polyphony");
        var after = before.Add("POLYPHONY");

        ReferenceEquals(before, after).ShouldBeTrue();
    }

    [Fact]
    public void Add_TrimsLeadingTrailingWhitespaceOnNewTag()
    {
        var s = TagSet.Empty.Add("  polyphony  ");
        s.ToArray().ShouldBe(["polyphony"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_NullOrWhitespace_Throws(string? tag)
    {
        Should.Throw<ArgumentException>(() => TagSet.Empty.Add(tag!));
    }

    // ─── Remove ─────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_PresentTag_ReturnsNewInstance_WithoutTag()
    {
        var before = TagSet.Parse("polyphony; twig");
        var after = before.Remove("polyphony");

        ReferenceEquals(before, after).ShouldBeFalse();
        after.ToArray().ShouldBe(["twig"]);
    }

    [Fact]
    public void Remove_AbsentTag_ReturnsSameInstance_NoOpSignal()
    {
        var before = TagSet.Parse("polyphony");
        var after = before.Remove("twig");

        ReferenceEquals(before, after).ShouldBeTrue();
    }

    [Fact]
    public void Remove_IsCaseInsensitive()
    {
        var before = TagSet.Parse("Polyphony; twig");
        var after = before.Remove("polyphony");

        ReferenceEquals(before, after).ShouldBeFalse();
        after.ToArray().ShouldBe(["twig"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Remove_NullOrWhitespace_ReturnsSameInstance(string? tag)
    {
        var before = TagSet.Parse("polyphony");
        var after = before.Remove(tag!);
        ReferenceEquals(before, after).ShouldBeTrue();
    }

    // ─── Format ─────────────────────────────────────────────────────────────

    [Fact]
    public void Format_Empty_ReturnsEmptyString()
    {
        TagSet.Empty.Format().ShouldBe(string.Empty);
    }

    [Fact]
    public void Format_Single_NoDelimiter()
    {
        TagSet.Parse("polyphony").Format().ShouldBe("polyphony");
    }

    [Fact]
    public void Format_Multiple_UsesSemicolonSpaceDelimiter()
    {
        TagSet.Parse("polyphony; polyphony:root; twig").Format()
            .ShouldBe("polyphony; polyphony:root; twig");
    }

    [Fact]
    public void Format_RoundTripsParse()
    {
        var original = "polyphony; polyphony:root; twig";
        TagSet.Parse(original).Format().ShouldBe(original);
    }

    // ─── Empty singleton ────────────────────────────────────────────────────

    [Fact]
    public void Empty_IsTheSameInstanceEachTime()
    {
        TagSet.Empty.ShouldBeSameAs(TagSet.Empty);
        TagSet.Empty.Count.ShouldBe(0);
    }

    [Fact]
    public void Empty_AfterAdd_IsNotMutated()
    {
        var before = TagSet.Empty;
        before.Add("polyphony");
        before.Count.ShouldBe(0);
    }
}
