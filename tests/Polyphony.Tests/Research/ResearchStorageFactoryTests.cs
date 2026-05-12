using Polyphony.Configuration;
using Polyphony.Research;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

public sealed class ResearchStorageFactoryTests
{
    // ── GitHub selection ─────────────────────────────────────────────────

    [Fact]
    public void Create_GitHubPlatform_ReturnsGitHubStorage()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Branch = "main",
            Platform = "github",
        };

        var storage = ResearchStorageFactory.Create(config);

        storage.ShouldBeOfType<GitHubResearchStorage>();
        storage.Target.Platform.ShouldBe("github");
        storage.Target.Repository.ShouldBe("myorg/research");
        storage.Target.Branch.ShouldBe("main");
    }

    // ── ADO selection ───────────────────────────────────────────────────

    [Fact]
    public void Create_AdoPlatform_ReturnsAdoStorage()
    {
        var config = new ResearchConfig
        {
            Repository = "MyProject/research-repo",
            Branch = "develop",
            Platform = "ado",
        };

        var storage = ResearchStorageFactory.Create(config);

        storage.ShouldBeOfType<AdoResearchStorage>();
        storage.Target.Platform.ShouldBe("ado");
        storage.Target.Repository.ShouldBe("MyProject/research-repo");
        storage.Target.Branch.ShouldBe("develop");
    }

    // ── Case-insensitive platform matching ──────────────────────────────

    [Theory]
    [InlineData("GitHub")]
    [InlineData("GITHUB")]
    [InlineData("github")]
    public void Create_GitHubCaseVariants_AllResolveToGitHub(string platform)
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Platform = platform,
        };

        var storage = ResearchStorageFactory.Create(config);

        storage.ShouldBeOfType<GitHubResearchStorage>();
        storage.Target.Platform.ShouldBe("github");
    }

    [Theory]
    [InlineData("ADO")]
    [InlineData("Ado")]
    [InlineData("ado")]
    public void Create_AdoCaseVariants_AllResolveToAdo(string platform)
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Platform = platform,
        };

        var storage = ResearchStorageFactory.Create(config);

        storage.ShouldBeOfType<AdoResearchStorage>();
        storage.Target.Platform.ShouldBe("ado");
    }

    // ── Whitespace trimming ─────────────────────────────────────────────

    [Fact]
    public void Create_TrimsWhitespace()
    {
        var config = new ResearchConfig
        {
            Repository = "  myorg/research  ",
            Branch = "  main  ",
            Platform = "  github  ",
        };

        var storage = ResearchStorageFactory.Create(config);

        storage.Target.Repository.ShouldBe("myorg/research");
        storage.Target.Branch.ShouldBe("main");
        storage.Target.Platform.ShouldBe("github");
    }

    // ── Default branch ──────────────────────────────────────────────────

    [Fact]
    public void Create_NullBranch_DefaultsToMain()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Platform = "github",
            Branch = null!,
        };

        var storage = ResearchStorageFactory.Create(config);

        storage.Target.Branch.ShouldBe("main");
    }

    // ── Error paths ─────────────────────────────────────────────────────

    [Fact]
    public void Create_NullConfig_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => ResearchStorageFactory.Create(null!));
    }

    [Fact]
    public void Create_MissingRepository_ThrowsArgumentException()
    {
        var config = new ResearchConfig { Platform = "github" };

        Should.Throw<ArgumentException>(() => ResearchStorageFactory.Create(config));
    }

    [Fact]
    public void Create_MissingPlatform_ThrowsArgumentException()
    {
        var config = new ResearchConfig { Repository = "myorg/research" };

        Should.Throw<ArgumentException>(() => ResearchStorageFactory.Create(config));
    }

    [Fact]
    public void Create_UnknownPlatform_ThrowsArgumentException()
    {
        var config = new ResearchConfig
        {
            Repository = "myorg/research",
            Platform = "bitbucket",
        };

        var ex = Should.Throw<ArgumentException>(() => ResearchStorageFactory.Create(config));
        ex.Message.ShouldContain("bitbucket");
    }

    // ── Source-on-ADO ↔ research-on-GitHub ───────────────────────────────

    [Fact]
    public void Create_CrossPlatform_SourceAdoResearchGitHub()
    {
        // This test verifies the architectural requirement: source code
        // lives on ADO but research archives on GitHub — no code change needed.
        var config = new ResearchConfig
        {
            Repository = "myorg/research-archive",
            Branch = "main",
            Platform = "github",
        };

        var storage = ResearchStorageFactory.Create(config);

        storage.ShouldBeOfType<GitHubResearchStorage>();
        storage.Target.Platform.ShouldBe("github");
    }

    [Fact]
    public void Create_CrossPlatform_SourceGitHubResearchAdo()
    {
        var config = new ResearchConfig
        {
            Repository = "MyProject/research-archive",
            Branch = "main",
            Platform = "ado",
        };

        var storage = ResearchStorageFactory.Create(config);

        storage.ShouldBeOfType<AdoResearchStorage>();
        storage.Target.Platform.ShouldBe("ado");
    }
}
