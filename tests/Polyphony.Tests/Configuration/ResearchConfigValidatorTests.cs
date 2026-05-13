using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ResearchConfigValidatorTests
{
    #region V-22: research.repository required and valid format

    [Fact]
    public void V22_NoResearchBlock_Accepts()
    {
        var profile = new ProfileConfig();

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void V22_ValidRepository_Accepts()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig { Repository = "PolyphonyRequiem/polyphony-research" }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void V22_NullRepository_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig { Repository = null }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.RepositoryRequiredRuleId);
        diagnostics[0].Severity.ShouldBe(ConfigValidationSeverity.Error);
        diagnostics[0].Message.ShouldContain("repository");
    }

    [Fact]
    public void V22_EmptyRepository_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig { Repository = "" }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.RepositoryRequiredRuleId);
    }

    [Fact]
    public void V22_MalformedRepository_NoSlash_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig { Repository = "just-a-name" }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.RepositoryRequiredRuleId);
        diagnostics[0].Message.ShouldContain("owner/repo");
    }

    [Fact]
    public void V22_MalformedRepository_WhitespaceInOwner_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig { Repository = "bad owner/repo" }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.RepositoryRequiredRuleId);
    }

    [Fact]
    public void V22_MalformedRepository_TrailingSlash_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig { Repository = "owner/" }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.RepositoryRequiredRuleId);
    }

    #endregion

    #region V-23: auth.token_env_var non-empty when auth block present

    [Fact]
    public void V23_NoAuthBlock_Accepts()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "owner/repo",
                Auth = null
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void V23_ValidTokenEnvVar_Accepts()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "owner/repo",
                Auth = new ResearchAuthConfig { TokenEnvVar = "RESEARCH_GH_TOKEN" }
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void V23_EmptyTokenEnvVar_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "owner/repo",
                Auth = new ResearchAuthConfig { TokenEnvVar = "" }
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.AuthTokenEnvVarEmptyRuleId);
        diagnostics[0].Severity.ShouldBe(ConfigValidationSeverity.Error);
    }

    [Fact]
    public void V23_NullTokenEnvVar_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "owner/repo",
                Auth = new ResearchAuthConfig { TokenEnvVar = null }
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.AuthTokenEnvVarEmptyRuleId);
    }

    #endregion

    #region V-24: base_path traversal rejection

    [Fact]
    public void V24_SimpleBasePath_Accepts()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "owner/repo",
                BasePath = "research/data"
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void V24_EmptyBasePath_Accepts()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "owner/repo",
                BasePath = ""
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void V24_DotDotTraversal_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "owner/repo",
                BasePath = "research/../etc"
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.BasePathTraversalRuleId);
    }

    [Fact]
    public void V24_LeadingSlash_Rejects()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "owner/repo",
                BasePath = "/absolute/path"
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.ShouldHaveSingleItem();
        diagnostics[0].RuleId.ShouldBe(ResearchConfigValidator.BasePathTraversalRuleId);
    }

    #endregion

    #region Multiple errors

    [Fact]
    public void MultipleErrors_AllSurfaced()
    {
        var profile = new ProfileConfig
        {
            Research = new ResearchConfig
            {
                Repository = "",
                BasePath = "../escape",
                Auth = new ResearchAuthConfig { TokenEnvVar = "" }
            }
        };

        var diagnostics = ResearchConfigValidator.Validate(profile);

        diagnostics.Count.ShouldBe(3);
        diagnostics.ShouldContain(d => d.RuleId == ResearchConfigValidator.RepositoryRequiredRuleId);
        diagnostics.ShouldContain(d => d.RuleId == ResearchConfigValidator.AuthTokenEnvVarEmptyRuleId);
        diagnostics.ShouldContain(d => d.RuleId == ResearchConfigValidator.BasePathTraversalRuleId);
    }

    #endregion
}
