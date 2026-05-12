using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ProfileConfigLoaderTests
{
    private readonly string _tempDir;

    public ProfileConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public void Load_WithResearchBlock_ParsesAllFields()
    {
        var yaml = """
            project:
              name: TestProject

            research:
              repository: myorg/research-archive
              branch: develop
              platform: github
            """;
        var path = WriteProfile(yaml);

        var config = ProfileConfigLoader.Load(path);

        config.Research.ShouldNotBeNull();
        config.Research.Repository.ShouldBe("myorg/research-archive");
        config.Research.Branch.ShouldBe("develop");
        config.Research.Platform.ShouldBe("github");
    }

    [Fact]
    public void Load_WithoutResearchBlock_ResearchIsNull()
    {
        var yaml = """
            project:
              name: TestProject
            """;
        var path = WriteProfile(yaml);

        var config = ProfileConfigLoader.Load(path);

        config.Research.ShouldBeNull();
    }

    [Fact]
    public void Load_BranchDefaultsToMain()
    {
        var yaml = """
            research:
              repository: myorg/archive
              platform: ado
            """;
        var path = WriteProfile(yaml);

        var config = ProfileConfigLoader.Load(path);

        config.Research.ShouldNotBeNull();
        config.Research.Branch.ShouldBe("main");
    }

    [Fact]
    public void Load_IgnoresUnmatchedProperties()
    {
        var yaml = """
            project:
              name: Polyphony
              description: test
              repository: PolyphonyRequiem/polyphony

            tech_stack:
              language: C#

            research:
              repository: myorg/archive
              platform: github
              some_future_field: value
            """;
        var path = WriteProfile(yaml);

        var config = ProfileConfigLoader.Load(path);

        config.Research.ShouldNotBeNull();
        config.Research.Repository.ShouldBe("myorg/archive");
    }

    // ── Error paths ─────────────────────────────────────────────────────

    [Fact]
    public void Load_FileNotFound_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(_tempDir, "missing.yaml");

        Should.Throw<FileNotFoundException>(() => ProfileConfigLoader.Load(missingPath));
    }

    [Fact]
    public void Load_MalformedYaml_ThrowsInvalidOperationException()
    {
        var yaml = """
            research:
              - this is not a valid mapping
              repository: bad
            """;
        var path = WriteProfile(yaml);

        var ex = Should.Throw<InvalidOperationException>(() => ProfileConfigLoader.Load(path));
        ex.Message.ShouldContain("line");
    }

    [Fact]
    public void Load_EmptyFile_ThrowsInvalidOperationException()
    {
        var path = WriteProfile("");

        var ex = Should.Throw<InvalidOperationException>(() => ProfileConfigLoader.Load(path));
        ex.Message.ShouldContain("empty");
    }

    // ── ADO platform ────────────────────────────────────────────────────

    [Fact]
    public void Load_AdoPlatform_ParsesCorrectly()
    {
        var yaml = """
            research:
              repository: MyProject/research-repo
              branch: main
              platform: ado
            """;
        var path = WriteProfile(yaml);

        var config = ProfileConfigLoader.Load(path);

        config.Research.ShouldNotBeNull();
        config.Research.Platform.ShouldBe("ado");
        config.Research.Repository.ShouldBe("MyProject/research-repo");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private string WriteProfile(string yaml)
    {
        var path = Path.Combine(_tempDir, "profile.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
