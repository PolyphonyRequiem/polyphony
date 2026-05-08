using Polyphony.Sdlc;
using Polyphony.Tagging;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for <see cref="FacetTagParser"/>. Round-trip with the
/// <c>polyphony:facets=&lt;csv&gt;</c> tag is the contract — the parser
/// is tolerant on read (case, whitespace, dedup) and strict on write.
/// </summary>
public sealed class FacetTagParserTests
{
    // ── ParseFacets / ParseTagValue: tolerant input, canonical output ────

    [Fact]
    public void ParseFacets_NullInput_ReturnsValidEmpty()
    {
        var result = FacetTagParser.ParseFacets(null);

        result.IsValid.ShouldBeTrue();
        result.Facets.ShouldBeEmpty();
        result.UnknownFacets.ShouldBeEmpty();
    }

    [Fact]
    public void ParseFacets_EmptySequence_ReturnsValidEmpty()
    {
        var result = FacetTagParser.ParseFacets([]);

        result.IsValid.ShouldBeTrue();
        result.Facets.ShouldBeEmpty();
    }

    [Fact]
    public void ParseFacets_WhitespaceTokens_AreSkipped()
    {
        var result = FacetTagParser.ParseFacets(["", "   ", "\t"]);

        result.IsValid.ShouldBeTrue();
        result.Facets.ShouldBeEmpty();
    }

    [Fact]
    public void ParseFacets_MixedCaseAndWhitespace_NormalisesToCanonical()
    {
        var result = FacetTagParser.ParseFacets(["  Implementable  ", "ACTIONABLE"]);

        result.IsValid.ShouldBeTrue();
        result.Facets.ShouldBe(["actionable", "implementable"]);
    }

    [Fact]
    public void ParseFacets_DuplicateTokens_AreDeduplicated()
    {
        var result = FacetTagParser.ParseFacets(["implementable", "implementable", "Implementable"]);

        result.IsValid.ShouldBeTrue();
        result.Facets.ShouldBe(["implementable"]);
    }

    [Fact]
    public void ParseFacets_AlphabeticalOrder_IsStable()
    {
        var fromA = FacetTagParser.ParseFacets(["plannable", "actionable", "implementable"]);
        var fromB = FacetTagParser.ParseFacets(["implementable", "plannable", "actionable"]);

        fromA.Facets.ShouldBe(fromB.Facets);
        fromA.Facets.ShouldBe(["actionable", "implementable", "plannable"]);
    }

    [Fact]
    public void ParseFacets_UnknownFacet_IsValidFalse_AndAllUnknownReported()
    {
        var result = FacetTagParser.ParseFacets(["actionable", "Bogus", "alsoBad"]);

        result.IsValid.ShouldBeFalse();
        result.Facets.ShouldBeEmpty();
        result.UnknownFacets.ShouldBe(["Bogus", "alsoBad"], ignoreOrder: true);
    }

    [Fact]
    public void ParseTagValue_EmptyOrNull_ReturnsValidEmpty()
    {
        FacetTagParser.ParseTagValue(null).IsValid.ShouldBeTrue();
        FacetTagParser.ParseTagValue("").IsValid.ShouldBeTrue();
        FacetTagParser.ParseTagValue("  ").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ParseTagValue_CommaSeparatedCsv_NormalisesAcrossDelimiter()
    {
        var result = FacetTagParser.ParseTagValue(" Implementable , ACTIONABLE ,implementable");

        result.IsValid.ShouldBeTrue();
        result.Facets.ShouldBe(["actionable", "implementable"]);
    }

    // ── FormatTag: strict on write ───────────────────────────────────────

    [Fact]
    public void FormatTag_SingleFacet_ProducesCanonicalShape()
    {
        FacetTagParser.FormatTag(["implementable"])
            .ShouldBe($"{PolyphonyTags.FacetsPrefix}=implementable");
    }

    [Fact]
    public void FormatTag_MixedCase_NormalisesToLower()
    {
        FacetTagParser.FormatTag(["Implementable", "ACTIONABLE"])
            .ShouldBe($"{PolyphonyTags.FacetsPrefix}=actionable,implementable");
    }

    [Fact]
    public void FormatTag_RoundTrip_IsStable()
    {
        var tag = FacetTagParser.FormatTag(["plannable", "implementable"]);
        var prefix = PolyphonyTags.FacetsPrefix + "=";
        var csv = tag[prefix.Length..];
        var parsed = FacetTagParser.ParseTagValue(csv);

        parsed.IsValid.ShouldBeTrue();
        FacetTagParser.FormatTag(parsed.Facets).ShouldBe(tag);
    }

    [Fact]
    public void FormatTag_UnknownFacet_Throws()
    {
        Should.Throw<ArgumentException>(() => FacetTagParser.FormatTag(["bogus"]));
    }

    [Fact]
    public void FormatTag_EmptyFacet_Throws()
    {
        Should.Throw<ArgumentException>(() => FacetTagParser.FormatTag([""]));
    }

    // ── TryExtract: TagSet round-trip ────────────────────────────────────

    [Fact]
    public void TryExtract_NoFacetTag_ReturnsNull()
    {
        var tags = TagSet.Parse("polyphony:planned; some-other-tag");
        FacetTagParser.TryExtract(tags).ShouldBeNull();
    }

    [Fact]
    public void TryExtract_PresentValidFacetTag_ReturnsParsedFacets()
    {
        var tags = TagSet.Parse($"polyphony:planned; {PolyphonyTags.FacetsPrefix}=actionable,implementable");
        var result = FacetTagParser.TryExtract(tags);

        result.ShouldNotBeNull();
        result!.IsValid.ShouldBeTrue();
        result.Facets.ShouldBe(["actionable", "implementable"]);
    }

    [Fact]
    public void TryExtract_PresentMalformedFacetTag_ReturnsInvalid()
    {
        var tags = TagSet.Parse($"{PolyphonyTags.FacetsPrefix}=actionable,bogus");
        var result = FacetTagParser.TryExtract(tags);

        result.ShouldNotBeNull();
        result!.IsValid.ShouldBeFalse();
        result.UnknownFacets.ShouldContain("bogus");
    }

    [Fact]
    public void TryExtract_PrefixMatchIsCaseInsensitive()
    {
        // Parents may ship tags with the prefix in any casing; we still find it.
        var tags = TagSet.Parse("POLYPHONY:Facets=implementable");
        var result = FacetTagParser.TryExtract(tags);

        result.ShouldNotBeNull();
        result!.IsValid.ShouldBeTrue();
        result.Facets.ShouldBe(["implementable"]);
    }
}
