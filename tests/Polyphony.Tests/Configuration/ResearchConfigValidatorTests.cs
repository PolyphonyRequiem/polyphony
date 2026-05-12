using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ResearchConfigValidatorTests
{
    // ── Absent block (opt-out) ──────────────────────────────────────────

    [Fact]
    public void Validate_NullConfig_Accepts()
    {
        var diagnostics = ResearchConfigValidator.Validate((ResearchConfig?)null);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NullProfileResearch_Accepts()
    {
        var profile = new ProfileConfig { Research = null };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldBeEmpty();
    }

    // ── Valid config ────────────────────────────────────────────────────

    [Fact]
    public void Validate_WellFormedGitHub_Accepts()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research-archive",
            Branch = "main",
            Platform = "github",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WellFormedAdo_Accepts()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research-archive",
            Branch = "develop",
            Platform = "ado",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_PlatformCaseInsensitive_Accepts()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research-archive",
            Platform = "GitHub",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldBeEmpty();
    }

    // ── Missing repository ──────────────────────────────────────────────

    [Fact]
    public void Validate_MissingRepository_RejectsV22()
    {
        var config = new ResearchConfig
        {
            Platform = "github",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldContain(d =>
            d.RuleId == ResearchConfigValidator.MissingRepositoryRuleId
            && d.Severity == ConfigValidationSeverity.Error);
    }

    [Fact]
    public void Validate_WhitespaceRepository_RejectsV22()
    {
        var config = new ResearchConfig
        {
            Repository = "   ",
            Platform = "github",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldContain(d =>
            d.RuleId == ResearchConfigValidator.MissingRepositoryRuleId);
    }

    // ── Missing platform ────────────────────────────────────────────────

    [Fact]
    public void Validate_MissingPlatform_RejectsV23()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldContain(d =>
            d.RuleId == ResearchConfigValidator.MissingPlatformRuleId
            && d.Severity == ConfigValidationSeverity.Error);
    }

    // ── Invalid platform ────────────────────────────────────────────────

    [Fact]
    public void Validate_UnknownPlatform_RejectsV24()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Platform = "bitbucket",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldContain(d =>
            d.RuleId == ResearchConfigValidator.InvalidPlatformRuleId
            && d.Severity == ConfigValidationSeverity.Error
            && d.Message.Contains("bitbucket"));
    }

    // ── Empty branch ────────────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyBranch_RejectsV25()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Platform = "github",
            Branch = "",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldContain(d =>
            d.RuleId == ResearchConfigValidator.EmptyBranchRuleId
            && d.Severity == ConfigValidationSeverity.Error);
    }

    [Fact]
    public void Validate_WhitespaceBranch_RejectsV25()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Platform = "github",
            Branch = "   ",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.ShouldContain(d =>
            d.RuleId == ResearchConfigValidator.EmptyBranchRuleId);
    }

    // ── Multiple errors ─────────────────────────────────────────────────

    [Fact]
    public void Validate_AllFieldsMissing_ReportsMultipleErrors()
    {
        var config = new ResearchConfig
        {
            Repository = null,
            Platform = null,
            Branch = "",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        diagnostics.Count.ShouldBe(3);
        diagnostics.ShouldContain(d => d.RuleId == ResearchConfigValidator.MissingRepositoryRuleId);
        diagnostics.ShouldContain(d => d.RuleId == ResearchConfigValidator.MissingPlatformRuleId);
        diagnostics.ShouldContain(d => d.RuleId == ResearchConfigValidator.EmptyBranchRuleId);
    }

    // ── Actionable messages ─────────────────────────────────────────────

    [Fact]
    public void Validate_MissingRepository_MessageIsActionable()
    {
        var config = new ResearchConfig { Platform = "github" };

        var diagnostics = ResearchConfigValidator.Validate(config);

        var diag = diagnostics.ShouldHaveSingleItem();
        diag.Message.ShouldContain("repository");
        diag.Message.ShouldContain("owner/repo");
    }

    [Fact]
    public void Validate_InvalidPlatform_MessageShowsValidValues()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Platform = "gitlab",
        };

        var diagnostics = ResearchConfigValidator.Validate(config);

        var diag = diagnostics.ShouldHaveSingleItem();
        diag.Message.ShouldContain("github");
        diag.Message.ShouldContain("ado");
    }
}
