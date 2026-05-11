using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ProfileConfigLoaderTests
{
    [Fact]
    public void LoadOrDefault_MissingFile_ReturnsDefaultConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}.yaml");

        var config = ProfileConfigLoader.LoadOrDefault(path);

        config.ShouldNotBeNull();
        config.Research.ShouldBeNull();
    }

    [Fact]
    public void LoadOrDefault_EmptyFile_ReturnsDefaultConfig()
    {
        var path = WriteTempProfile("");

        var config = ProfileConfigLoader.LoadOrDefault(path);

        config.ShouldNotBeNull();
        config.Research.ShouldBeNull();
    }

    [Fact]
    public void LoadOrDefault_NoResearchBlock_ReturnsNullResearch()
    {
        var path = WriteTempProfile("""
            project:
              name: TestProject
            """);

        var config = ProfileConfigLoader.LoadOrDefault(path);

        config.ShouldNotBeNull();
        config.Research.ShouldBeNull();
    }

    [Fact]
    public void LoadOrDefault_ResearchDisabled_ParsesCorrectly()
    {
        var path = WriteTempProfile("""
            research:
              enabled: false
              repository: owner/repo
            """);

        var config = ProfileConfigLoader.LoadOrDefault(path);

        config.Research.ShouldNotBeNull();
        config.Research!.Enabled.ShouldBeFalse();
        config.Research.Repository.ShouldBe("owner/repo");
    }

    [Fact]
    public void LoadOrDefault_ResearchEnabled_ParsesAllFields()
    {
        var path = WriteTempProfile("""
            research:
              enabled: true
              repository: PolyphonyRequiem/polyphony-research
              platform: github
              default_branch: develop
            """);

        var config = ProfileConfigLoader.LoadOrDefault(path);

        config.Research.ShouldNotBeNull();
        config.Research!.Enabled.ShouldBeTrue();
        config.Research.Repository.ShouldBe("PolyphonyRequiem/polyphony-research");
        config.Research.Platform.ShouldBe("github");
        config.Research.DefaultBranch.ShouldBe("develop");
    }

    [Fact]
    public void LoadOrDefault_ResearchMinimal_DefaultsApplied()
    {
        var path = WriteTempProfile("""
            research:
              enabled: true
              repository: owner/repo
            """);

        var config = ProfileConfigLoader.LoadOrDefault(path);

        config.Research.ShouldNotBeNull();
        config.Research!.Platform.ShouldBeNull();
        config.Research.DefaultBranch.ShouldBe("main");
    }

    [Fact]
    public void LoadOrDefault_AdoPlatform_ParsesThreeSegmentRepo()
    {
        var path = WriteTempProfile("""
            research:
              enabled: true
              repository: MyOrg/MyProject/research-repo
              platform: ado
            """);

        var config = ProfileConfigLoader.LoadOrDefault(path);

        config.Research.ShouldNotBeNull();
        config.Research!.Repository.ShouldBe("MyOrg/MyProject/research-repo");
        config.Research.Platform.ShouldBe("ado");
    }

    [Fact]
    public void LoadOrDefault_IgnoresUnmatchedProperties()
    {
        var path = WriteTempProfile("""
            project:
              name: TestProject
              description: Some description
            tech_stack:
              language: C#
            build:
              restore: dotnet restore
            research:
              enabled: true
              repository: owner/repo
            mcp_servers:
              - twig-mcp
            """);

        var config = ProfileConfigLoader.LoadOrDefault(path);

        config.Research.ShouldNotBeNull();
        config.Research!.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void LoadOrDefault_RoundTrip_PreservesResearchConfig()
    {
        var yaml = """
            research:
              enabled: true
              repository: PolyphonyRequiem/polyphony-research
              platform: github
              default_branch: develop
            """;
        var path = WriteTempProfile(yaml);

        var config1 = ProfileConfigLoader.LoadOrDefault(path);

        // Serialize back and reload
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .Build();
        var roundTripYaml = serializer.Serialize(config1);
        var roundTripPath = WriteTempProfile(roundTripYaml);
        var config2 = ProfileConfigLoader.LoadOrDefault(roundTripPath);

        config2.Research.ShouldNotBeNull();
        config2.Research!.Enabled.ShouldBeTrue();
        config2.Research.Repository.ShouldBe("PolyphonyRequiem/polyphony-research");
        config2.Research.Platform.ShouldBe("github");
        config2.Research.DefaultBranch.ShouldBe("develop");
    }

    [Fact]
    public void LoadOrDefaultFromRepo_ConstructsCorrectPath()
    {
        // Create a temp directory structure mimicking a repo
        var tempRoot = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid()}");
        var configDir = Path.Combine(tempRoot, ".polyphony-config");
        Directory.CreateDirectory(configDir);

        File.WriteAllText(Path.Combine(configDir, "profile.yaml"), """
            research:
              enabled: true
              repository: owner/repo
            """);

        try
        {
            var config = ProfileConfigLoader.LoadOrDefaultFromRepo(tempRoot);

            config.Research.ShouldNotBeNull();
            config.Research!.Enabled.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string WriteTempProfile(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-test-{Guid.NewGuid()}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
