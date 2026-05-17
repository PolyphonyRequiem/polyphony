using Polyphony.Tagging;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Tagging;

/// <summary>
/// Truth table for <see cref="PolyphonyTags.IsInScope"/> and
/// <see cref="PolyphonyTags.IsRoot"/>. The interesting axis is the
/// disjoint-tag rule: bare <c>polyphony</c> is in-scope but not root,
/// <c>polyphony:root</c> is BOTH (root implies in-scope), and
/// <c>polyphony:planned</c> alone is neither (it's a status sub-tag).
/// </summary>
public sealed class PolyphonyTagsTests
{
    [Theory]
    [InlineData("", false, false)]
    [InlineData("polyphony", true, false)]
    [InlineData("polyphony:root", true, true)]
    [InlineData("polyphony:planned", false, false)]
    [InlineData("polyphony; twig", true, false)]
    [InlineData("polyphony:root; polyphony", true, true)]
    [InlineData("polyphony; polyphony:planned", true, false)]
    [InlineData("twig; polyphony:planned", false, false)]
    public void TruthTable(string tagsRaw, bool expectInScope, bool expectRoot)
    {
        var tags = TagSet.Parse(tagsRaw);
        PolyphonyTags.IsInScope(tags).ShouldBe(expectInScope);
        PolyphonyTags.IsRoot(tags).ShouldBe(expectRoot);
    }

    [Fact]
    public void Constants_MatchSpec()
    {
        // Constants are part of the JSON contract — pin them explicitly.
        PolyphonyTags.InScope.ShouldBe("polyphony");
        PolyphonyTags.Root.ShouldBe("polyphony:root");
        PolyphonyTags.Planned.ShouldBe("polyphony:planned");
        PolyphonyTags.ImplMergedInMgPrefix.ShouldBe("polyphony:impl-merged-in-mg");
    }

    [Fact]
    public void IsInScope_IsCaseInsensitive()
    {
        var upper = TagSet.Parse("POLYPHONY");
        PolyphonyTags.IsInScope(upper).ShouldBeTrue();

        var mixedRoot = TagSet.Parse("Polyphony:Root");
        PolyphonyTags.IsRoot(mixedRoot).ShouldBeTrue();
        PolyphonyTags.IsInScope(mixedRoot).ShouldBeTrue();
    }

    [Theory]
    [InlineData("pg-1", "polyphony:impl-merged-in-mg=pg-1")]
    [InlineData("PG-1", "polyphony:impl-merged-in-mg=pg-1")]
    [InlineData("  pg-1  ", "polyphony:impl-merged-in-mg=pg-1")]
    [InlineData("pg-1/pg-2", "polyphony:impl-merged-in-mg=pg-1/pg-2")]
    [InlineData("PG-1/PG-2", "polyphony:impl-merged-in-mg=pg-1/pg-2")]
    public void ImplMergedInMg_NormalizesAndComposes(string input, string expected)
    {
        PolyphonyTags.ImplMergedInMg(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ImplMergedInMg_EmptyOrWhitespace_ReturnsEmpty(string? input)
    {
        // Callers MUST guard against this rather than write a malformed
        // bare-prefix tag — verify the contract that empty in => empty out.
        PolyphonyTags.ImplMergedInMg(input!).ShouldBe(string.Empty);
        PolyphonyTags.NormalizeMergeGroupKey(input).ShouldBe(string.Empty);
    }

    [Fact]
    public void HasImplMergedInMg_TagPresentForSameKey_ReturnsTrue()
    {
        var tags = TagSet.Parse("polyphony:root; polyphony:impl-merged-in-mg=pg-1");
        PolyphonyTags.HasImplMergedInMg(tags, "pg-1").ShouldBeTrue();
        // Casing on the lookup key must not matter.
        PolyphonyTags.HasImplMergedInMg(tags, "PG-1").ShouldBeTrue();
    }

    [Fact]
    public void HasImplMergedInMg_TagPresentForDifferentKey_ReturnsFalse()
    {
        // Multi-MG: an apex could plausibly carry markers for several MGs
        // simultaneously (e.g. apex root participates in pg-1 and pg-2).
        // Each key must look up independently — pg-1's presence does not
        // imply pg-2.
        var tags = TagSet.Parse(
            "polyphony:root; polyphony:impl-merged-in-mg=pg-1; polyphony:impl-merged-in-mg=pg-2");
        PolyphonyTags.HasImplMergedInMg(tags, "pg-1").ShouldBeTrue();
        PolyphonyTags.HasImplMergedInMg(tags, "pg-2").ShouldBeTrue();
        PolyphonyTags.HasImplMergedInMg(tags, "pg-3").ShouldBeFalse();
    }

    [Fact]
    public void HasImplMergedInMg_EmptyKey_ReturnsFalse()
    {
        // Defense against the empty-key composer contract: empty key
        // cannot match anything, even a malformed bare-prefix tag.
        var tags = TagSet.Parse("polyphony:impl-merged-in-mg=");
        PolyphonyTags.HasImplMergedInMg(tags, "").ShouldBeFalse();
        PolyphonyTags.HasImplMergedInMg(tags, null!).ShouldBeFalse();
    }
}
