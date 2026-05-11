using NSubstitute;
using Polyphony.Configuration;
using Polyphony.Research;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

public sealed class ResearchStoreFactoryTests
{
    private readonly ResearchStoreFactory _factory = new();

    // ──────────────────────────────────────────────────────────────────────
    // Null config (disabled) → null store
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_NullConfig_ReturnsNull()
    {
        var store = _factory.Create(null);
        store.ShouldBeNull();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Platform routing: GitHub
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_GitHubPlatform_ReturnsGitHubStore()
    {
        var config = new EffectiveResearchConfig(
            Repository: "owner/repo",
            Platform: "github",
            DefaultBranch: "main");
        var ghClient = Substitute.For<IGitHubResearchClient>();

        var store = _factory.Create(config, gitHubClient: ghClient);

        store.ShouldNotBeNull();
        store.ShouldBeAssignableTo<IResearchStore>();
    }

    [Fact]
    public void Create_GitHubPlatform_NoClient_Throws()
    {
        var config = new EffectiveResearchConfig(
            Repository: "owner/repo",
            Platform: "github",
            DefaultBranch: "main");

        Should.Throw<InvalidOperationException>(() =>
            _factory.Create(config, gitHubClient: null));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Platform routing: ADO
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_AdoPlatform_ReturnsAdoStore()
    {
        var config = new EffectiveResearchConfig(
            Repository: "org/project/repo",
            Platform: "ado",
            DefaultBranch: "main");
        var adoClient = Substitute.For<IAdoResearchClient>();

        var store = _factory.Create(config, adoClient: adoClient);

        store.ShouldNotBeNull();
        store.ShouldBeAssignableTo<IResearchStore>();
    }

    [Fact]
    public void Create_AdoPlatform_NoClient_Throws()
    {
        var config = new EffectiveResearchConfig(
            Repository: "org/project/repo",
            Platform: "ado",
            DefaultBranch: "main");

        Should.Throw<InvalidOperationException>(() =>
            _factory.Create(config, adoClient: null));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Unknown platform
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_UnknownPlatform_Throws()
    {
        var config = new EffectiveResearchConfig(
            Repository: "owner/repo",
            Platform: "bitbucket",
            DefaultBranch: "main");

        Should.Throw<InvalidOperationException>(() =>
            _factory.Create(config));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Cross-platform routing: source on ADO, research on GitHub (and inverse)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_SourceAdoResearchGitHub_ProducesGitHubStore()
    {
        // source project is on ADO, but research repo is explicitly GitHub
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "owner/repo",
            Platform = "github"
        };
        var processConfig = new ProcessConfig { Platform = "ado" };
        var effective = ResearchConfigResolver.Resolve(research, processConfig);

        var ghClient = Substitute.For<IGitHubResearchClient>();
        var store = _factory.Create(effective, gitHubClient: ghClient);

        store.ShouldNotBeNull();
        effective!.Platform.ShouldBe("github");
    }

    [Fact]
    public void Create_SourceGitHubResearchAdo_ProducesAdoStore()
    {
        // source project is on GitHub, but research repo is explicitly ADO
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "org/project/repo",
            Platform = "ado"
        };
        var processConfig = new ProcessConfig { Platform = "github" };
        var effective = ResearchConfigResolver.Resolve(research, processConfig);

        var adoClient = Substitute.For<IAdoResearchClient>();
        var store = _factory.Create(effective, adoClient: adoClient);

        store.ShouldNotBeNull();
        effective!.Platform.ShouldBe("ado");
    }

    [Fact]
    public void Create_SourceGitHubResearchDefault_FallsBackToGitHub()
    {
        // source is GitHub, research platform omitted → defaults to GitHub
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "owner/repo",
            Platform = null
        };
        var processConfig = new ProcessConfig { Platform = "github" };
        var effective = ResearchConfigResolver.Resolve(research, processConfig);

        var ghClient = Substitute.For<IGitHubResearchClient>();
        var store = _factory.Create(effective, gitHubClient: ghClient);

        store.ShouldNotBeNull();
        effective!.Platform.ShouldBe("github");
    }

    [Fact]
    public void Create_SourceAdoResearchDefault_FallsBackToAdo()
    {
        // source is ADO, research platform omitted → defaults to ADO
        var research = new ResearchConfig
        {
            Enabled = true,
            Repository = "org/project/repo",
            Platform = null
        };
        var processConfig = new ProcessConfig { Platform = "ado" };
        var effective = ResearchConfigResolver.Resolve(research, processConfig);

        var adoClient = Substitute.For<IAdoResearchClient>();
        var store = _factory.Create(effective, adoClient: adoClient);

        store.ShouldNotBeNull();
        effective!.Platform.ShouldBe("ado");
    }
}
