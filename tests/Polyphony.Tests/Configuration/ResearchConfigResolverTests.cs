using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ResearchConfigResolverTests
{
    [Fact]
    public void Resolve_NullResearch_ReturnsNull()
    {
        var process = MinimalProcessConfig();

        var result = ResearchConfigResolver.Resolve(null, process);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_DisabledResearch_ReturnsNull()
    {
        var research = new ResearchConfig { Enabled = false };
        var process = MinimalProcessConfig();

        var result = ResearchConfigResolver.Resolve(research, process);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ExplicitPlatform_UsesExplicit()
    {
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "owner/repo",
            Platform = "github"
        };
        var process = MinimalProcessConfig("ado");

        var result = ResearchConfigResolver.Resolve(research, process);

        result.ShouldNotBeNull();
        result!.Platform.ShouldBe("github");
    }

    [Fact]
    public void Resolve_OmittedPlatform_FallsBackToProcessConfig()
    {
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "org/project/repo",
            Platform = null
        };
        var process = MinimalProcessConfig("ado");

        var result = ResearchConfigResolver.Resolve(research, process);

        result.ShouldNotBeNull();
        result!.Platform.ShouldBe("ado");
    }

    [Fact]
    public void Resolve_EmptyPlatform_FallsBackToProcessConfig()
    {
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "owner/repo",
            Platform = ""
        };
        var process = MinimalProcessConfig("github");

        var result = ResearchConfigResolver.Resolve(research, process);

        result.ShouldNotBeNull();
        result!.Platform.ShouldBe("github");
    }

    [Fact]
    public void Resolve_DefaultBranch_DefaultsToMain()
    {
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "owner/repo"
        };
        var process = MinimalProcessConfig();

        var result = ResearchConfigResolver.Resolve(research, process);

        result.ShouldNotBeNull();
        result!.DefaultBranch.ShouldBe("main");
    }

    [Fact]
    public void Resolve_ExplicitDefaultBranch_Preserved()
    {
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "owner/repo",
            DefaultBranch = "develop"
        };
        var process = MinimalProcessConfig();

        var result = ResearchConfigResolver.Resolve(research, process);

        result.ShouldNotBeNull();
        result!.DefaultBranch.ShouldBe("develop");
    }

    [Fact]
    public void Resolve_Repository_Preserved()
    {
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "PolyphonyRequiem/polyphony-research"
        };
        var process = MinimalProcessConfig();

        var result = ResearchConfigResolver.Resolve(research, process);

        result.ShouldNotBeNull();
        result!.Repository.ShouldBe("PolyphonyRequiem/polyphony-research");
    }

    private static ProcessConfig MinimalProcessConfig(string platform = "github")
    {
        return new ProcessConfig
        {
            ProcessTemplate = "Basic",
            Platform = platform,
            Types = new() { ["Task"] = new TypeConfig { Facets = ["implementable"] } },
        };
    }
}
