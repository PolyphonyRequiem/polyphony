using Polyphony.Configuration;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ProfileConfigValidatorTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Disabled / absent — always valid
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_NullResearch_IsValid()
    {
        var profile = new ProfileConfig { Research = null };
        var process = MinimalProcessConfig();

        var result = ProfileConfigValidator.Validate(profile, process);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_DisabledResearch_IsValid()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig { Enabled = false }
        };
        var process = MinimalProcessConfig();

        var result = ProfileConfigValidator.Validate(profile, process);

        result.IsValid.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────
    // V-R1: repository required when enabled
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VR1_MissingRepository_ProducesError(string? repo)
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig { Enabled = true, Repository = repo }
        };
        var process = MinimalProcessConfig();

        var result = ProfileConfigValidator.Validate(profile, process);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-R1");
    }

    // ──────────────────────────────────────────────────────────────────────
    // V-R2: platform must be valid
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void VR2_InvalidExplicitPlatform_ProducesError()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Enabled = true,
                Repository = "owner/repo",
                Platform = "bitbucket"
            }
        };
        var process = MinimalProcessConfig();

        var result = ProfileConfigValidator.Validate(profile, process);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-R2");
    }

    [Fact]
    public void VR2_ValidExplicitPlatform_NoError()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Enabled = true,
                Repository = "owner/repo",
                Platform = "github"
            }
        };
        var process = MinimalProcessConfig();

        var result = ProfileConfigValidator.Validate(profile, process);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-R2");
    }

    [Fact]
    public void VR2_OmittedPlatform_FallsBackToProcessConfig()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Enabled = true,
                Repository = "owner/repo",
                Platform = null
            }
        };
        var process = MinimalProcessConfig("github");

        var result = ProfileConfigValidator.Validate(profile, process);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-R2");
    }

    [Fact]
    public void VR2_OmittedPlatformWithInvalidProcessPlatform_ProducesError()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Enabled = true,
                Repository = "owner/repo",
                Platform = null
            }
        };
        var process = MinimalProcessConfig("invalid");

        var result = ProfileConfigValidator.Validate(profile, process);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-R2");
    }

    // ──────────────────────────────────────────────────────────────────────
    // V-R3: repository format must match platform
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("github", "owner/repo", true)]
    [InlineData("github", "owner/repo/extra", false)]
    [InlineData("github", "onlyone", false)]
    [InlineData("ado", "org/project/repo", true)]
    [InlineData("ado", "org/repo", false)]
    [InlineData("ado", "only", false)]
    [InlineData("ado", "a/b/c/d", false)]
    public void VR3_RepositoryFormat_ValidatedByPlatform(
        string platform, string repository, bool shouldPass)
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Enabled = true,
                Repository = repository,
                Platform = platform
            }
        };
        var process = MinimalProcessConfig();

        var result = ProfileConfigValidator.Validate(profile, process);

        if (shouldPass)
        {
            result.Errors.ShouldNotContain(d => d.RuleId == "V-R3");
        }
        else
        {
            result.Errors.ShouldContain(d => d.RuleId == "V-R3");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Fully valid config
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_FullyValidGitHubConfig_IsValid()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Enabled = true,
                Repository = "PolyphonyRequiem/polyphony-research",
                Platform = "github",
                DefaultBranch = "main"
            }
        };
        var process = MinimalProcessConfig();

        var result = ProfileConfigValidator.Validate(profile, process);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_FullyValidAdoConfig_IsValid()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Enabled = true,
                Repository = "MyOrg/MyProject/research-repo",
                Platform = "ado",
                DefaultBranch = "main"
            }
        };
        var process = MinimalProcessConfig();

        var result = ProfileConfigValidator.Validate(profile, process);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static ProcessConfig MinimalProcessConfig(string platform = "github") =>
        new ProcessConfigBuilder()
            .WithPlatform(platform)
            .WithType("Task", ["implementable"], new() { ["begin"] = "Doing" })
            .Build();
}
