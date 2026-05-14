using Polyphony.Tagging;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Tagging;

public sealed class PolyphonyTagTests
{
    // ─── TryParse ────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_InScope_ReturnsInScope()
    {
        var tag = PolyphonyTag.TryParse("polyphony");
        tag.ShouldBeOfType<PolyphonyTag.InScope>();
    }

    [Fact]
    public void TryParse_Root_ReturnsRoot()
    {
        var tag = PolyphonyTag.TryParse("polyphony:root");
        tag.ShouldBeOfType<PolyphonyTag.Root>();
    }

    [Fact]
    public void TryParse_Planned_ReturnsPlanned()
    {
        var tag = PolyphonyTag.TryParse("polyphony:planned");
        tag.ShouldBeOfType<PolyphonyTag.Planned>();
    }

    [Fact]
    public void TryParse_Facets_ReturnsFacetsWithNames()
    {
        var tag = PolyphonyTag.TryParse("polyphony:facets=actionable,plannable");
        var facets = tag.ShouldBeOfType<PolyphonyTag.Facets>();
        facets.FacetNames.ShouldBe(["actionable", "plannable"]);
    }

    [Fact]
    public void TryParse_CaseInsensitive_ReturnsCorrectVariant()
    {
        PolyphonyTag.TryParse("Polyphony").ShouldBeOfType<PolyphonyTag.InScope>();
        PolyphonyTag.TryParse("POLYPHONY:ROOT").ShouldBeOfType<PolyphonyTag.Root>();
        PolyphonyTag.TryParse("Polyphony:Planned").ShouldBeOfType<PolyphonyTag.Planned>();
    }

    [Fact]
    public void TryParse_NonPolyphonyTag_ReturnsNull()
    {
        PolyphonyTag.TryParse("twig").ShouldBeNull();
        PolyphonyTag.TryParse("some-other-tag").ShouldBeNull();
        PolyphonyTag.TryParse("").ShouldBeNull();
        PolyphonyTag.TryParse(null!).ShouldBeNull();
    }

    // ─── ToTagString ─────────────────────────────────────────────────────

    [Fact]
    public void ToTagString_RoundTrips_FixedVariants()
    {
        PolyphonyTag.ToTagString(new PolyphonyTag.InScope()).ShouldBe("polyphony");
        PolyphonyTag.ToTagString(new PolyphonyTag.Root()).ShouldBe("polyphony:root");
        PolyphonyTag.ToTagString(new PolyphonyTag.Planned()).ShouldBe("polyphony:planned");
    }

    [Fact]
    public void ToTagString_Facets_FormatsCorrectly()
    {
        var tag = new PolyphonyTag.Facets(["actionable", "plannable"]);
        PolyphonyTag.ToTagString(tag).ShouldBe("polyphony:facets=actionable,plannable");
    }

    // ─── AllOwned ────────────────────────────────────────────────────────

    [Fact]
    public void AllOwned_MixedTags_ReturnsOnlyPolyphonyOwned()
    {
        var tagSet = TagSet.Parse("polyphony; polyphony:root; twig; polyphony:planned");
        var parsed = PolyphonyTag.AllOwned(tagSet);

        parsed.Count.ShouldBe(3);
        parsed[0].ShouldBeOfType<PolyphonyTag.InScope>();
        parsed[1].ShouldBeOfType<PolyphonyTag.Root>();
        parsed[2].ShouldBeOfType<PolyphonyTag.Planned>();
    }

    [Fact]
    public void AllOwned_EmptyTagSet_ReturnsEmpty()
    {
        var parsed = PolyphonyTag.AllOwned(TagSet.Empty);
        parsed.ShouldBeEmpty();
    }

    // ─── IsPolyphonyOwned ────────────────────────────────────────────────

    [Theory]
    [InlineData("polyphony", true)]
    [InlineData("polyphony:root", true)]
    [InlineData("polyphony:planned", true)]
    [InlineData("polyphony:facets=actionable", true)]
    [InlineData("polyphony:anything-future", true)]
    [InlineData("POLYPHONY:ROOT", true)]
    [InlineData("twig", false)]
    [InlineData("some-tag", false)]
    [InlineData("", false)]
    public void IsPolyphonyOwned_ClassifiesCorrectly(string raw, bool expected)
    {
        PolyphonyTag.IsPolyphonyOwned(raw).ShouldBe(expected);
    }
}
