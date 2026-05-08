using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

/// <summary>
/// Tests for <see cref="PlanFileFrontMatter"/>. Three-way Status
/// (Absent/Malformed/Present) is the contract; the parser must NOT
/// silently fall back to Absent on bad YAML — that would mask architect
/// errors.
/// </summary>
public sealed class PlanFileFrontMatterTests
{
    // ── Absent ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullOrEmpty_IsAbsent()
    {
        PlanFileFrontMatter.Parse(null).Status.ShouldBe(PlanFileFrontMatterStatus.Absent);
        PlanFileFrontMatter.Parse("").Status.ShouldBe(PlanFileFrontMatterStatus.Absent);
    }

    [Fact]
    public void Parse_NoFence_IsAbsent()
    {
        const string body = "# Plan title\n\nSome prose without front-matter.\n";
        PlanFileFrontMatter.Parse(body).Status.ShouldBe(PlanFileFrontMatterStatus.Absent);
    }

    [Fact]
    public void Parse_EmptyFence_IsAbsent()
    {
        const string body = "---\n---\n# body\n";
        PlanFileFrontMatter.Parse(body).Status.ShouldBe(PlanFileFrontMatterStatus.Absent);
    }

    [Fact]
    public void Parse_FenceNotAtStart_IsAbsent()
    {
        // Leading prose disqualifies the fence (prevents misreading
        // arbitrary mid-document YAML blocks as front-matter).
        const string body = "intro line\n---\napex_facets:\n  - implementable\n---\n";
        PlanFileFrontMatter.Parse(body).Status.ShouldBe(PlanFileFrontMatterStatus.Absent);
    }

    // ── Present ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_PresentWithoutApexFacets_HasEmptyList()
    {
        const string body = "---\nother_key: value\n---\n# body\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Present);
        result.ApexFacets.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_PresentWithSingleFacet_FlowSequence()
    {
        const string body = "---\napex_facets: [implementable]\n---\n# body\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Present);
        result.ApexFacets.ShouldBe(["implementable"]);
    }

    [Fact]
    public void Parse_PresentWithSingleFacet_BlockSequence()
    {
        const string body = "---\napex_facets:\n  - implementable\n---\n# body\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Present);
        result.ApexFacets.ShouldBe(["implementable"]);
    }

    [Fact]
    public void Parse_PresentWithMultipleFacets_NormalisesAlphabetical()
    {
        const string body = "---\napex_facets:\n  - implementable\n  - actionable\n---\n# body\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Present);
        result.ApexFacets.ShouldBe(["actionable", "implementable"]);
    }

    [Fact]
    public void Parse_PresentWithMixedCase_LowercasesAndDedupes()
    {
        const string body = "---\napex_facets:\n  - Implementable\n  - implementable\n---\n# body\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Present);
        result.ApexFacets.ShouldBe(["implementable"]);
    }

    [Fact]
    public void Parse_UnknownKeysIgnored_StillPresent()
    {
        // Forward-compat: unknown front-matter keys must not trip the parser.
        const string body = "---\napex_facets: [implementable]\nfuture_field: 42\n---\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Present);
        result.ApexFacets.ShouldBe(["implementable"]);
    }

    // ── Malformed ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_BadYaml_IsMalformed()
    {
        const string body = "---\napex_facets: [implementable\n---\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Malformed);
        result.ErrorDetail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_ApexFacetsScalar_IsMalformed()
    {
        const string body = "---\napex_facets: implementable\n---\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Malformed);
        result.ErrorDetail.ShouldNotBeNull();
        result.ErrorDetail.ShouldContain("apex_facets");
    }

    [Fact]
    public void Parse_ApexFacetsMapping_IsMalformed()
    {
        const string body = "---\napex_facets:\n  primary: implementable\n---\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Malformed);
    }

    [Fact]
    public void Parse_ApexFacetsUnknownToken_IsMalformed_AndReportsToken()
    {
        const string body = "---\napex_facets:\n  - bogus\n---\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Malformed);
        result.UnknownFacets.ShouldContain("bogus");
        result.ErrorDetail.ShouldNotBeNull();
        result.ErrorDetail.ShouldContain("bogus");
    }

    [Fact]
    public void Parse_RootIsSequenceNotMapping_IsMalformed()
    {
        const string body = "---\n- a\n- b\n---\n";
        var result = PlanFileFrontMatter.Parse(body);

        result.Status.ShouldBe(PlanFileFrontMatterStatus.Malformed);
    }
}
