using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <see cref="ValidateConfigCommand"/>.
/// Tests cover valid configs, missing transitions, schema version errors,
/// missing template files (warnings), and custom --config paths.
/// </summary>
public sealed class ValidateConfigCommandTests : IDisposable
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory, "TestFixtures", "ProcessConfigs");

    private readonly List<string> _tempDirs = [];

    private string CreateConfigDir(string yamlContent)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-vc-{Guid.NewGuid():N}");
        var conductorDir = Path.Combine(tempDir, ".conductor");
        Directory.CreateDirectory(conductorDir);
        File.WriteAllText(Path.Combine(conductorDir, "process-config.yaml"), yamlContent);
        _tempDirs.Add(tempDir);
        return conductorDir;
    }

    private string CreateConfigDirFromFixture(string fixtureName)
    {
        var fixturePath = Path.Combine(FixturesDir, fixtureName);
        var yaml = File.ReadAllText(fixturePath);
        return CreateConfigDir(yaml);
    }

    private static (int ExitCode, string Output) CaptureConsole(Func<int> action)
    {
        ConsoleTestLock.AsyncLock.Wait();
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exitCode = action();
                return (exitCode, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }

    #region Valid configs — all 4 process templates

    [Theory]
    [InlineData("basic.yaml")]
    [InlineData("agile.yaml")]
    [InlineData("scrum.yaml")]
    [InlineData("cmmi.yaml")]
    public void ValidConfig_ReturnsSuccessExitCode(string fixture)
    {
        var configDir = CreateConfigDirFromFixture(fixture);
        var cmd = new ValidateConfigCommand();

        var (exitCode, _) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        exitCode.ShouldBe(ExitCodes.Success);
    }

    [Theory]
    [InlineData("basic.yaml")]
    [InlineData("agile.yaml")]
    [InlineData("scrum.yaml")]
    [InlineData("cmmi.yaml")]
    public void ValidConfig_OutputsIsValidTrue(string fixture)
    {
        var configDir = CreateConfigDirFromFixture(fixture);
        var cmd = new ValidateConfigCommand();

        var (_, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    #endregion

    #region Config with missing transitions — exit code non-zero, errors[] non-empty

    [Fact]
    public void MissingTransitions_ReturnsConfigErrorExitCode()
    {
        const string yaml = """
            process_template: Basic
            types:
              Epic:
                facets: [plannable]
              Task:
                facets: [implementable]
            transitions:
              Epic:
                begin_planning: Doing
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (exitCode, _) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        exitCode.ShouldBe(ExitCodes.ConfigError);
    }

    [Fact]
    public void MissingTransitions_ErrorsArrayIsNonEmpty()
    {
        const string yaml = """
            process_template: Basic
            types:
              Epic:
                facets: [plannable]
              Task:
                facets: [implementable]
            transitions:
              Epic:
                begin_planning: Doing
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (_, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldContain(e => e.RuleId == "V-5");
    }

    #endregion

    #region Config with schema_version: 99 — exit code ConfigError

    [Fact]
    public void SchemaVersion99_ReturnsConfigErrorExitCode()
    {
        const string yaml = """
            schema_version: 99
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions:
              Task:
                begin_implementation: Doing
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (exitCode, _) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        exitCode.ShouldBe(ExitCodes.ConfigError);
    }

    [Fact]
    public void SchemaVersion99_OutputsErrorWithConfigRuleId()
    {
        const string yaml = """
            schema_version: 99
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions:
              Task:
                begin_implementation: Doing
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (_, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
        result.Errors.Length.ShouldBe(1);
        result.Errors[0].RuleId.ShouldBe("CONFIG");
        result.Errors[0].Message.ShouldContain("schema");
    }

    #endregion

    #region Config with missing template files — exit code 0, warnings[] non-empty

    [Fact]
    public void MissingTemplateFiles_ReturnsSuccessExitCode()
    {
        var configDir = CreateConfigDirFromFixture("basic.yaml");
        var cmd = new ValidateConfigCommand();

        var (exitCode, _) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        exitCode.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public void MissingTemplateFiles_WarningsArrayIsNonEmpty()
    {
        var configDir = CreateConfigDirFromFixture("basic.yaml");
        var cmd = new ValidateConfigCommand();

        var (_, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldNotBeEmpty();
    }

    #endregion

    #region --config parameter loads from specified directory

    [Fact]
    public void ConfigParameter_LoadsFromSpecifiedDirectory()
    {
        var configDir = CreateConfigDirFromFixture("agile.yaml");
        var cmd = new ValidateConfigCommand();

        var (exitCode, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ConfigParameter_NonexistentDirectory_ReturnsConfigError()
    {
        var cmd = new ValidateConfigCommand();
        var bogusPath = Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}");

        var (exitCode, _) = CaptureConsole(() => cmd.ValidateConfig(bogusPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
    }

    [Fact]
    public void ConfigParameter_NonexistentDirectory_OutputsErrorJson()
    {
        var cmd = new ValidateConfigCommand();
        var bogusPath = Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}");

        var (_, output) = CaptureConsole(() => cmd.ValidateConfig(bogusPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    #endregion

    #region Human output format

    [Fact]
    public void HumanOutput_ValidConfig_PrintsValidMessage()
    {
        var configDir = CreateConfigDirFromFixture("basic.yaml");
        var cmd = new ValidateConfigCommand();

        var (exitCode, output) = CaptureConsole(() => cmd.ValidateConfig(configDir, "human"));

        exitCode.ShouldBe(ExitCodes.Success);
        output.ShouldContain("valid");
    }

    [Fact]
    public void HumanOutput_InvalidConfig_PrintsErrorCount()
    {
        const string yaml = """
            process_template: Basic
            types:
              Epic:
                facets: [plannable]
            transitions: {}
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (exitCode, output) = CaptureConsole(() => cmd.ValidateConfig(configDir, "human"));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        output.ShouldContain("error");
    }

    #endregion

    #region JSON output uses snake_case property names

    [Fact]
    public void JsonOutput_UsesSnakeCasePropertyNames()
    {
        var configDir = CreateConfigDirFromFixture("basic.yaml");
        var cmd = new ValidateConfigCommand();

        var (_, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        output.ShouldContain("\"is_valid\"");
        output.ShouldContain("\"errors\"");
        output.ShouldContain("\"warnings\"");
    }

    #endregion

    #region V-17 / V-18 — legacy pg_branch / pg_pr deprecation warnings

    [Fact]
    public void LegacyPgBranchKey_EmitsV17Warning()
    {
        const string yaml = """
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions:
              Task:
                begin_implementation: Doing
            branch_strategy:
              feature_branch: "feature/{root_id}"
              pg_branch: "pg-{n}/{root_id}-{slug}"
              target: main
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (exitCode, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.RuleId == "V-17");
    }

    [Fact]
    public void NewMgBranchKey_DoesNotEmitV17Warning()
    {
        const string yaml = """
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions:
              Task:
                begin_implementation: Doing
            branch_strategy:
              feature_branch: "feature/{root_id}"
              mg_branch: "mg-{n}/{root_id}-{slug}"
              target: main
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (_, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.Warnings.ShouldNotContain(w => w.RuleId == "V-17");
    }

    [Fact]
    public void LegacyPgPrPolicyKey_EmitsV18Warning()
    {
        const string yaml = """
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions:
              Task:
                begin_implementation: Doing
            review_policies:
              implementation:
                pg_pr: { agent_review: true, human_review: false, auto_merge: true }
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (exitCode, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.RuleId == "V-18");
    }

    [Fact]
    public void NewMgPrPolicyKey_DoesNotEmitV18Warning()
    {
        const string yaml = """
            process_template: Basic
            types:
              Task:
                facets: [implementable]
            transitions:
              Task:
                begin_implementation: Doing
            review_policies:
              implementation:
                mg_pr: { agent_review: true, human_review: false, auto_merge: true }
            """;
        var configDir = CreateConfigDir(yaml);
        var cmd = new ValidateConfigCommand();

        var (_, output) = CaptureConsole(() => cmd.ValidateConfig(configDir));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ConfigValidationResult);
        result.ShouldNotBeNull();
        result.Warnings.ShouldNotContain(w => w.RuleId == "V-18");
    }

    #endregion

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); }
            catch { /* best-effort cleanup */ }
        }
    }
}

