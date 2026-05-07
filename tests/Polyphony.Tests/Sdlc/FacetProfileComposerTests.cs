using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

public sealed class FacetProfileComposerTests
{
    [Fact]
    public void Compose_NoFacets_ReturnsEmpty()
    {
        var addendum = FacetProfileComposer.Compose(
            facets: [],
            profiles: Profiles());

        addendum.Skills.ShouldBeEmpty();
        addendum.Mcps.ShouldBeEmpty();
        addendum.GuidanceContext.ShouldBeNull();
    }

    [Fact]
    public void Compose_SingleFacet_ReturnsItsSkillsAndMcps()
    {
        var profiles = Profiles(
            ("actionable", new FacetProfile(["evidence", "security"], ["shell"])));

        var addendum = FacetProfileComposer.Compose(["actionable"], profiles);

        addendum.Skills.ShouldBe(new[] { "evidence", "security" });
        addendum.Mcps.ShouldBe(new[] { "shell" });
        addendum.GuidanceContext.ShouldBeNull();
    }

    [Fact]
    public void Compose_MultipleFacets_UnionsAndDedupesIdentical()
    {
        var profiles = Profiles(
            ("actionable", new FacetProfile(["evidence", "shell-skill"], ["shell", "web-fetch"])),
            ("implementable", new FacetProfile(["coding-style", "shell-skill"], ["shell"])));

        var addendum = FacetProfileComposer.Compose(["actionable", "implementable"], profiles);

        addendum.Skills.ShouldBe(new[] { "coding-style", "evidence", "shell-skill" });
        addendum.Mcps.ShouldBe(new[] { "shell", "web-fetch" });
    }

    [Fact]
    public void Compose_UnknownFacet_SilentlyOmits()
    {
        var profiles = Profiles(
            ("actionable", new FacetProfile(["evidence"], ["shell"])));

        var addendum = FacetProfileComposer.Compose(
            facets: ["actionable", "fictional"],
            profiles: profiles);

        addendum.Skills.ShouldBe(new[] { "evidence" });
        addendum.Mcps.ShouldBe(new[] { "shell" });
    }

    [Fact]
    public void Compose_GuidanceProvided_PassesThroughVerbatim()
    {
        const string guidance =
            "Use the Foo library, not the Bar library.\n" +
            "Reviewer: pay extra attention to error messages.";

        var addendum = FacetProfileComposer.Compose(
            facets: [],
            profiles: Profiles(),
            perItemGuidance: guidance);

        addendum.GuidanceContext.ShouldBe(guidance);
    }

    [Fact]
    public void Compose_GuidanceNullOrEmpty_ResultGuidanceIsNullOrEmpty()
    {
        var addendumNull = FacetProfileComposer.Compose([], Profiles(), perItemGuidance: null);
        addendumNull.GuidanceContext.ShouldBeNull();

        var addendumEmpty = FacetProfileComposer.Compose([], Profiles(), perItemGuidance: "");
        addendumEmpty.GuidanceContext.ShouldBe("");
    }

    [Fact]
    public void Compose_OutputOrder_SkillsAndMcpsSortedAscending()
    {
        var profiles = Profiles(
            ("actionable", new FacetProfile(["zeta", "alpha", "mu"], ["zeta", "alpha", "mu"])),
            ("implementable", new FacetProfile(["beta"], ["beta"])));

        var addendum = FacetProfileComposer.Compose(
            facets: ["actionable", "implementable"],
            profiles: profiles);

        addendum.Skills.ShouldBe(new[] { "alpha", "beta", "mu", "zeta" });
        addendum.Mcps.ShouldBe(new[] { "alpha", "beta", "mu", "zeta" });
    }

    [Fact]
    public void Compose_DuplicateFacetsInInput_DedupedSilently()
    {
        var profiles = Profiles(
            ("actionable", new FacetProfile(["evidence"], ["shell"])));

        var addendum = FacetProfileComposer.Compose(
            facets: ["actionable", "actionable"],
            profiles: profiles);

        addendum.Skills.ShouldBe(new[] { "evidence" });
        addendum.Mcps.ShouldBe(new[] { "shell" });
    }

    [Fact]
    public void Compose_NullFacets_Throws()
    {
        Should.Throw<ArgumentNullException>(() => FacetProfileComposer.Compose(
            facets: null!,
            profiles: Profiles()));
    }

    [Fact]
    public void Compose_NullProfiles_Throws()
    {
        Should.Throw<ArgumentNullException>(() => FacetProfileComposer.Compose(
            facets: [],
            profiles: null!));
    }

    private static IReadOnlyDictionary<string, FacetProfile> Profiles(
        params (string Name, FacetProfile Profile)[] entries)
    {
        var dict = new Dictionary<string, FacetProfile>(StringComparer.Ordinal);
        foreach (var (name, profile) in entries)
        {
            dict[name] = profile;
        }
        return dict;
    }
}
