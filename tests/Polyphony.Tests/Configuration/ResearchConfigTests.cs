using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ResearchConfigTests
{
    #region Minimal valid block

    [Fact]
    public void Parse_MinimalValidBlock_Succeeds()
    {
        var config = ResearchConfigLoader.Parse(MinimalValidYaml)!;

        config.ShouldNotBeNull();
        config.Repo.ShouldBe("owner/research-repo");
        config.Auth.ShouldNotBeNull();
        config.Auth!.EnvVar.ShouldBe("RESEARCH_PAT");
    }

    [Fact]
    public void Validate_MinimalValidBlock_IsValid()
    {
        var config = ResearchConfigLoader.Parse(MinimalValidYaml)!;
        ResearchConfigLoader.ApplyDefaults(config);

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    #endregion

    #region Full block

    [Fact]
    public void Parse_FullBlock_AllFieldsPopulated()
    {
        var config = ResearchConfigLoader.Parse(FullValidYaml)!;

        config.ShouldNotBeNull();
        config.Repo.ShouldBe("PolyphonyRequiem/polyphony-research");
        config.Branch.ShouldBe("develop");
        config.Platform.ShouldBe("ado");
        config.Auth.ShouldNotBeNull();
        config.Auth!.EnvVar.ShouldBe("MY_PAT");
        config.Paths.ShouldNotBeNull();
        config.Paths!.ArchiveRoot.ShouldBe("docs/research/");
        config.Paths.ScratchRoot.ShouldBe("tmp/scratch/");
    }

    [Fact]
    public void Validate_FullBlock_IsValid()
    {
        var config = ResearchConfigLoader.Parse(FullValidYaml)!;

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    #endregion

    #region Missing research block

    [Fact]
    public void Parse_NoResearchBlock_ReturnsNull()
    {
        var config = ResearchConfigLoader.Parse("""
            project:
              name: SomeProject
            """);

        config.ShouldBeNull();
    }

    [Fact]
    public void LoadOrDefault_MissingFile_ReturnsNull()
    {
        var result = ResearchConfigLoader.LoadOrDefault(
            Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}", "profile.yaml"));

        result.ShouldBeNull();
    }

    #endregion

    #region R-1: repo required

    [Fact]
    public void R1_MissingRepo_ProducesError()
    {
        var config = ValidConfig();
        config.Repo = null;

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-1");
    }

    [Fact]
    public void R1_EmptyRepo_ProducesError()
    {
        var config = ValidConfig();
        config.Repo = "";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-1");
    }

    [Fact]
    public void R1_WhitespaceRepo_ProducesError()
    {
        var config = ValidConfig();
        config.Repo = "   ";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-1");
    }

    #endregion

    #region R-2: repo must be owner/name shaped

    [Fact]
    public void R2_ValidRepo_NoError()
    {
        var config = ValidConfig();
        config.Repo = "org/repo";

        var result = ResearchConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "R-2");
    }

    [Fact]
    public void R2_NoSlash_ProducesError()
    {
        var config = ValidConfig();
        config.Repo = "justaname";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-2");
    }

    [Fact]
    public void R2_TooManySlashes_ProducesError()
    {
        var config = ValidConfig();
        config.Repo = "org/repo/extra";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-2");
    }

    [Fact]
    public void R2_EmptyOwner_ProducesError()
    {
        var config = ValidConfig();
        config.Repo = "/repo";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-2");
    }

    [Fact]
    public void R2_EmptyName_ProducesError()
    {
        var config = ValidConfig();
        config.Repo = "org/";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-2");
    }

    [Fact]
    public void R2_WhitespaceInOwner_ProducesError()
    {
        var config = ValidConfig();
        config.Repo = "my org/repo";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-2");
    }

    #endregion

    #region R-3: platform must be known

    [Fact]
    public void R3_GitHub_NoError()
    {
        var config = ValidConfig();
        config.Platform = ResearchPlatform.GitHub;

        var result = ResearchConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "R-3");
    }

    [Fact]
    public void R3_Ado_NoError()
    {
        var config = ValidConfig();
        config.Platform = ResearchPlatform.Ado;

        var result = ResearchConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "R-3");
    }

    [Fact]
    public void R3_UnknownPlatform_ProducesError()
    {
        var config = ValidConfig();
        config.Platform = "gitlab";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d =>
            d.RuleId == "R-3" && d.Message.Contains("gitlab"));
    }

    #endregion

    #region R-4: auth required

    [Fact]
    public void R4_MissingAuth_ProducesError()
    {
        var config = ValidConfig();
        config.Auth = null;

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-4");
    }

    #endregion

    #region R-5: auth.env_var required when auth present

    [Fact]
    public void R5_EmptyEnvVar_ProducesError()
    {
        var config = ValidConfig();
        config.Auth!.EnvVar = "";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-5");
    }

    [Fact]
    public void R5_NullEnvVar_ProducesError()
    {
        var config = ValidConfig();
        config.Auth!.EnvVar = null;

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-5");
    }

    [Fact]
    public void R5_WhitespaceEnvVar_ProducesError()
    {
        var config = ValidConfig();
        config.Auth!.EnvVar = "   ";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-5");
    }

    #endregion

    #region R-6: paths must be POSIX-style and non-absolute

    [Fact]
    public void R6_BackslashInArchiveRoot_ProducesError()
    {
        var config = ValidConfig();
        config.Paths!.ArchiveRoot = @"research\archive\";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d =>
            d.RuleId == "R-6" && d.Message.Contains("archive_root"));
    }

    [Fact]
    public void R6_BackslashInScratchRoot_ProducesError()
    {
        var config = ValidConfig();
        config.Paths!.ScratchRoot = @"research\scratch\";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d =>
            d.RuleId == "R-6" && d.Message.Contains("scratch_root"));
    }

    [Fact]
    public void R6_AbsoluteUnixArchiveRoot_ProducesError()
    {
        var config = ValidConfig();
        config.Paths!.ArchiveRoot = "/research/archive/";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d =>
            d.RuleId == "R-6" && d.Message.Contains("absolute"));
    }

    [Fact]
    public void R6_AbsoluteWindowsArchiveRoot_ProducesError()
    {
        var config = ValidConfig();
        config.Paths!.ArchiveRoot = "C:/research/archive/";

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d =>
            d.RuleId == "R-6" && d.Message.Contains("absolute"));
    }

    [Fact]
    public void R6_RelativePosixPath_NoError()
    {
        var config = ValidConfig();
        config.Paths!.ArchiveRoot = "docs/research/";
        config.Paths.ScratchRoot = "tmp/scratch/";

        var result = ResearchConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "R-6");
    }

    #endregion

    #region Defaults application

    [Fact]
    public void ApplyDefaults_NullPaths_CreatesDefaultPaths()
    {
        var config = ValidConfig();
        config.Paths = null;

        ResearchConfigLoader.ApplyDefaults(config);

        config.Paths.ShouldNotBeNull();
        config.Paths!.ArchiveRoot.ShouldBe("research/");
        config.Paths.ScratchRoot.ShouldBe("research/scratch/");
    }

    [Fact]
    public void ApplyDefaults_ExistingPaths_PreservesValues()
    {
        var config = ValidConfig();
        config.Paths!.ArchiveRoot = "custom/archive/";
        config.Paths.ScratchRoot = "custom/scratch/";

        ResearchConfigLoader.ApplyDefaults(config);

        config.Paths.ArchiveRoot.ShouldBe("custom/archive/");
        config.Paths.ScratchRoot.ShouldBe("custom/scratch/");
    }

    [Fact]
    public void Defaults_BranchIsMain()
    {
        var config = new ResearchConfig();

        config.Branch.ShouldBe("main");
    }

    [Fact]
    public void Defaults_PlatformIsGitHub()
    {
        var config = new ResearchConfig();

        config.Platform.ShouldBe("github");
    }

    #endregion

    #region Round-trip (research block only)

    [Fact]
    public void RoundTrip_MinimalBlock_PreservesUserInputPlusDefaults()
    {
        var original = ResearchConfigLoader.Parse(MinimalValidYaml)!;
        ResearchConfigLoader.ApplyDefaults(original);

        var serialized = ResearchConfigLoader.Serialize(original);
        var reloaded = ResearchConfigLoader.Parse(serialized)!;
        ResearchConfigLoader.ApplyDefaults(reloaded);

        reloaded.Repo.ShouldBe(original.Repo);
        reloaded.Branch.ShouldBe(original.Branch);
        reloaded.Platform.ShouldBe(original.Platform);
        reloaded.Auth!.EnvVar.ShouldBe(original.Auth!.EnvVar);
        reloaded.Paths!.ArchiveRoot.ShouldBe(original.Paths!.ArchiveRoot);
        reloaded.Paths.ScratchRoot.ShouldBe(original.Paths.ScratchRoot);
    }

    [Fact]
    public void RoundTrip_FullBlock_PreservesAllValues()
    {
        var original = ResearchConfigLoader.Parse(FullValidYaml)!;

        var serialized = ResearchConfigLoader.Serialize(original);
        var reloaded = ResearchConfigLoader.Parse(serialized)!;

        reloaded.Repo.ShouldBe(original.Repo);
        reloaded.Branch.ShouldBe(original.Branch);
        reloaded.Platform.ShouldBe(original.Platform);
        reloaded.Auth!.EnvVar.ShouldBe(original.Auth!.EnvVar);
        reloaded.Paths!.ArchiveRoot.ShouldBe(original.Paths!.ArchiveRoot);
        reloaded.Paths.ScratchRoot.ShouldBe(original.Paths.ScratchRoot);
    }

    #endregion

    #region LoadOrDefault integration

    [Fact]
    public void LoadOrDefault_ValidFile_ReturnsConfig()
    {
        var path = WriteTempFile(MinimalValidYaml);

        var config = ResearchConfigLoader.LoadOrDefault(path);

        config.ShouldNotBeNull();
        config!.Repo.ShouldBe("owner/research-repo");
    }

    [Fact]
    public void LoadOrDefault_InvalidConfig_Throws()
    {
        var yaml = """
            research:
              repo: invalid-no-slash
              auth:
                env_var: PAT
            """;
        var path = WriteTempFile(yaml);

        Should.Throw<InvalidOperationException>(() =>
            ResearchConfigLoader.LoadOrDefault(path));
    }

    #endregion

    #region Multiple errors reported

    [Fact]
    public void Validate_MultipleIssues_AllReported()
    {
        var config = new ResearchConfig
        {
            Repo = null,
            Platform = "bitbucket",
            Auth = null,
        };

        var result = ResearchConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "R-1");
        result.Errors.ShouldContain(d => d.RuleId == "R-3");
        result.Errors.ShouldContain(d => d.RuleId == "R-4");
    }

    #endregion

    #region ResearchPlatform constants

    [Fact]
    public void ResearchPlatform_IsValid_AcceptsGitHub()
    {
        ResearchPlatform.IsValid("github").ShouldBeTrue();
    }

    [Fact]
    public void ResearchPlatform_IsValid_AcceptsAdo()
    {
        ResearchPlatform.IsValid("ado").ShouldBeTrue();
    }

    [Fact]
    public void ResearchPlatform_IsValid_RejectsUnknown()
    {
        ResearchPlatform.IsValid("gitlab").ShouldBeFalse();
    }

    #endregion

    #region Helpers

    private static ResearchConfig ValidConfig() => new()
    {
        Repo = "owner/research-repo",
        Branch = "main",
        Platform = "github",
        Auth = new ResearchAuthConfig { EnvVar = "RESEARCH_PAT" },
        Paths = new ResearchPathsConfig
        {
            ArchiveRoot = "research/",
            ScratchRoot = "research/scratch/",
        },
    };

    private const string MinimalValidYaml = """
        research:
          repo: owner/research-repo
          auth:
            env_var: RESEARCH_PAT
        """;

    private const string FullValidYaml = """
        research:
          repo: PolyphonyRequiem/polyphony-research
          branch: develop
          platform: ado
          auth:
            env_var: MY_PAT
          paths:
            archive_root: docs/research/
            scratch_root: tmp/scratch/
        """;

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyphony-test-{Guid.NewGuid()}", "profile.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    #endregion
}
