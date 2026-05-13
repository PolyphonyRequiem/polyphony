using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ProfileConfigLoaderTests
{
    [Fact]
    public void Load_MissingFile_ReturnsEmptyConfig()
    {
        var result = ProfileConfigLoader.Load(Path.Combine(Path.GetTempPath(), "nonexistent-profile.yaml"));

        result.ShouldNotBeNull();
        result.Research.ShouldBeNull();
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyConfig()
    {
        var path = CreateTempYaml("");

        try
        {
            var result = ProfileConfigLoader.Load(path);

            result.ShouldNotBeNull();
            result.Research.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NoResearchBlock_ReturnsNullResearch()
    {
        var yaml = """
            project:
              name: TestProject
              repository: owner/repo
            tech_stack:
              language: C#
            """;

        var path = CreateTempYaml(yaml);

        try
        {
            var result = ProfileConfigLoader.Load(path);

            result.ShouldNotBeNull();
            result.Research.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ResearchBlockMinimal_ParsesRepository()
    {
        var yaml = """
            research:
              repository: PolyphonyRequiem/polyphony-research
            """;

        var path = CreateTempYaml(yaml);

        try
        {
            var result = ProfileConfigLoader.Load(path);

            result.Research.ShouldNotBeNull();
            result.Research!.Repository.ShouldBe("PolyphonyRequiem/polyphony-research");
            result.Research.BasePath.ShouldBe("");
            result.Research.Branch.ShouldBe("main");
            result.Research.Auth.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ResearchBlockFull_ParsesAllFields()
    {
        var yaml = """
            research:
              repository: PolyphonyRequiem/polyphony-research
              base_path: data/runs
              branch: develop
              auth:
                token_env_var: RESEARCH_GH_TOKEN
            """;

        var path = CreateTempYaml(yaml);

        try
        {
            var result = ProfileConfigLoader.Load(path);

            result.Research.ShouldNotBeNull();
            result.Research!.Repository.ShouldBe("PolyphonyRequiem/polyphony-research");
            result.Research.BasePath.ShouldBe("data/runs");
            result.Research.Branch.ShouldBe("develop");
            result.Research.Auth.ShouldNotBeNull();
            result.Research.Auth!.TokenEnvVar.ShouldBe("RESEARCH_GH_TOKEN");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ResearchWithOtherBlocks_IgnoresUnrecognized()
    {
        var yaml = """
            project:
              name: Polyphony
              description: test
              repository: PolyphonyRequiem/polyphony
            tech_stack:
              language: C#
            research:
              repository: PolyphonyRequiem/polyphony-research
            conventions:
              - sealed classes
            """;

        var path = CreateTempYaml(yaml);

        try
        {
            var result = ProfileConfigLoader.Load(path);

            result.Research.ShouldNotBeNull();
            result.Research!.Repository.ShouldBe("PolyphonyRequiem/polyphony-research");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => ProfileConfigLoader.Load(null!));
    }

    [Fact]
    public void Load_EmptyPath_Throws()
    {
        Should.Throw<ArgumentException>(() => ProfileConfigLoader.Load(""));
    }

    private static string CreateTempYaml(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-test-{Guid.NewGuid()}.yaml");
        File.WriteAllText(path, content);
        return path;
    }
}
