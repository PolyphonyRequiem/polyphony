using Polyphony.Configuration;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc;

public sealed class FacetProfileValidatorTests
{
    [Fact]
    public void Validator_NoFacetsBlock_Accepts()
    {
        var config = MinimalConfig().Build();

        var diagnostics = FacetProfileValidator.Validate(config);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validator_EmptyFacetsBlock_Accepts()
    {
        var config = MinimalConfig().Build();
        config.Facets = new Dictionary<string, FacetProfileConfig>();

        var diagnostics = FacetProfileValidator.Validate(config);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validator_DistinctSkillsAndMcps_Accepts()
    {
        var config = MinimalConfig()
            .WithFacetProfile("actionable", skills: ["evidence", "security"], mcps: ["shell", "web-fetch"])
            .Build();

        var diagnostics = FacetProfileValidator.Validate(config);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validator_IdenticalCollision_Accepts()
    {
        // Cross-facet identical names dedupe silently at compose time;
        // the validator MUST NOT flag them.
        var config = MinimalConfig()
            .WithFacetProfile("actionable", skills: ["shared"], mcps: ["shell"])
            .WithFacetProfile("implementable", skills: ["shared"], mcps: ["shell"])
            .Build();

        var diagnostics = FacetProfileValidator.Validate(config);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validator_DifferentValueCollision_Rejects()
    {
        // For plain string lists, "different value for same name" is not
        // representable across facets — they're either present or not. The
        // meaningful within-config typo we CAN catch is a duplicate name
        // inside ONE facet's list. That's V-20.
        var config = MinimalConfig()
            .WithFacetProfile("actionable", skills: ["evidence", "evidence", "security"])
            .Build();

        var diagnostics = FacetProfileValidator.Validate(config);

        diagnostics.ShouldHaveSingleItem();
        var diag = diagnostics[0];
        diag.RuleId.ShouldBe(FacetProfileValidator.DuplicateWithinFacetRuleId);
        diag.Severity.ShouldBe(ConfigValidationSeverity.Error);
        diag.Message.ShouldContain("actionable");
        diag.Message.ShouldContain("evidence");
        diag.Message.ShouldContain("skill");
    }

    [Fact]
    public void Validator_DuplicateMcpWithinFacet_Rejects()
    {
        var config = MinimalConfig()
            .WithFacetProfile("actionable", mcps: ["shell", "shell"])
            .Build();

        var diagnostics = FacetProfileValidator.Validate(config);

        diagnostics.ShouldHaveSingleItem();
        var diag = diagnostics[0];
        diag.RuleId.ShouldBe(FacetProfileValidator.DuplicateWithinFacetRuleId);
        diag.Message.ShouldContain("actionable");
        diag.Message.ShouldContain("shell");
        diag.Message.ShouldContain("mcp");
    }

    [Fact]
    public void Validator_DuplicateAcrossSkillsAndMcps_TwoErrors()
    {
        var config = MinimalConfig()
            .WithFacetProfile(
                "actionable",
                skills: ["evidence", "evidence"],
                mcps: ["shell", "shell"])
            .Build();

        var diagnostics = FacetProfileValidator.Validate(config);

        diagnostics.Count.ShouldBe(2);
        diagnostics.ShouldContain(d => d.Message.Contains("skill"));
        diagnostics.ShouldContain(d => d.Message.Contains("mcp"));
    }

    [Fact]
    public void Validator_TripleDuplicate_ReportsOnce()
    {
        // Repeated occurrences of the same duplicate name within one list
        // surface a single diagnostic — operators don't want N copies of
        // the same complaint.
        var config = MinimalConfig()
            .WithFacetProfile("actionable", skills: ["evidence", "evidence", "evidence"])
            .Build();

        var diagnostics = FacetProfileValidator.Validate(config);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].Message.ShouldContain("evidence");
    }

    [Fact]
    public void Validator_PluggedIntoConfigValidator_SurfacesV20()
    {
        var config = MinimalConfig()
            .WithFacetProfile("actionable", skills: ["evidence", "evidence"])
            .Build();

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == FacetProfileValidator.DuplicateWithinFacetRuleId);
    }

    private static ProcessConfigBuilder MinimalConfig()
    {
        return new ProcessConfigBuilder()
            .WithType(
                "Task",
                facets: ["implementable"],
                transitions: new Dictionary<string, string> { ["begin_implementation"] = "Doing" });
    }
}
