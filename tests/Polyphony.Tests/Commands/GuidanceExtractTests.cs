using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony guidance extract</c>. Verifies the
/// happy path (description block + ADO field), per-type policy resolution,
/// and the standard not-found / config-error contracts.
/// </summary>
public sealed class GuidanceExtractTests : CommandTestBase
{
    private const string OpenTag = "<!-- polyphony:guidance -->";
    private const string CloseTag = "<!-- /polyphony:guidance -->";

    private GuidanceCommands CreateCommand() => new(Repository);

    [Fact]
    public async Task Extract_WorkItemNotFound_EmitsErrorJson_CacheError()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();

        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Extract(99_900, fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.CacheError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString()!.ShouldContain("99900");
        doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_900);
    }

    [Fact]
    public async Task Extract_DescriptionBlock_DefaultPolicy_ExtractsBlock()
    {
        using var fx = new PolicyFileFixture();
        // No policy file → built-in defaults apply (description_block).
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_500)
                .WithType("Issue")
                .WithTitle("Has guidance")
                .WithField("System.Description", $"intro\n{OpenTag}\nUse Polly for retries.\n{CloseTag}\noutro")
                .Build());

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Extract(10_500, fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.GuidanceExtractResult);
        result.ShouldNotBeNull();
        result.WorkItemId.ShouldBe(10_500);
        result.Source.ShouldBe("description_block");
        result.Guidance.ShouldBe("Use Polly for retries.");
        result.GuidancePresent.ShouldBeTrue();
    }

    [Fact]
    public async Task Extract_DescriptionBlock_NoBlockPresent_GuidancePresentFalse()
    {
        using var fx = new PolicyFileFixture();
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_501)
                .WithType("Issue")
                .WithTitle("No guidance")
                .WithField("System.Description", "Just a regular description.")
                .Build());

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Extract(10_501, fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.GuidanceExtractResult);
        result.ShouldNotBeNull();
        result.GuidancePresent.ShouldBeFalse();
        result.Guidance.ShouldBeNull();
        // Null fields must be omitted from raw JSON (snake_case + WhenWritingNull).
        output.ShouldNotContain("\"guidance\":null");
    }

    [Fact]
    public async Task Extract_AdoFieldSource_PolicyOverride_ExtractsCustomField()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            guidance:
              source: ado_field
              ado_field_name: Custom.Guidance
            """);

        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_502)
                .WithType("Issue")
                .WithTitle("Has ADO guidance")
                .WithField("Custom.Guidance", "Use OpenTelemetry.")
                // Description has a fenced block but should be IGNORED under ado_field source.
                .WithField("System.Description", $"{OpenTag}\nIgnored\n{CloseTag}")
                .Build());

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Extract(10_502, fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.GuidanceExtractResult);
        result.ShouldNotBeNull();
        result.Source.ShouldBe("ado_field");
        result.Guidance.ShouldBe("Use OpenTelemetry.");
        result.GuidancePresent.ShouldBeTrue();
    }

    [Fact]
    public async Task Extract_PerTypeOverride_AppliesByWorkItemType()
    {
        using var fx = new PolicyFileFixture();
        // Workspace default is description_block; Task overrides to ado_field.
        fx.WritePolicy("""
            schema_version: 1
            guidance:
              source: description_block
              by_type:
                Task:
                  source: ado_field
                  ado_field_name: Custom.TaskGuidance
            """);

        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_510)
                .WithType("Issue")
                .WithTitle("Issue with description block")
                .WithField("System.Description", $"{OpenTag}\nIssue text.\n{CloseTag}")
                .Build(),
            new WorkItemBuilder()
                .WithId(10_511)
                .WithType("Task")
                .WithTitle("Task with ADO field")
                .WithField("Custom.TaskGuidance", "Task text.")
                // Description with a block — must NOT be returned for Task scope.
                .WithField("System.Description", $"{OpenTag}\nIgnored for Task.\n{CloseTag}")
                .Build());

        var cmd = CreateCommand();
        var (issueExit, issueOutput) = await CaptureConsoleAsync(() => cmd.Extract(10_510, fx.PolicyPath));
        var (taskExit, taskOutput) = await CaptureConsoleAsync(() => cmd.Extract(10_511, fx.PolicyPath));

        issueExit.ShouldBe(ExitCodes.Success);
        var issueResult = JsonSerializer.Deserialize(issueOutput, PolyphonyJsonContext.Default.GuidanceExtractResult);
        issueResult!.Source.ShouldBe("description_block");
        issueResult.Guidance.ShouldBe("Issue text.");

        taskExit.ShouldBe(ExitCodes.Success);
        var taskResult = JsonSerializer.Deserialize(taskOutput, PolyphonyJsonContext.Default.GuidanceExtractResult);
        taskResult!.Source.ShouldBe("ado_field");
        taskResult.Guidance.ShouldBe("Task text.");
    }

    [Fact]
    public async Task Extract_PolicyMisconfigured_AdoFieldWithoutFieldName_EmitsConfigError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            guidance:
              source: ado_field
            """);

        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_520)
                .WithType("Issue")
                .WithTitle("ok")
                .Build());

        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.Extract(10_520, fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString()!.ShouldContain("ado_field_name");
    }

    [Fact]
    public async Task Extract_JsonContract_SnakeCaseKeys_NoPascalCaseLeakage()
    {
        using var fx = new PolicyFileFixture();
        await SeedAsync(
            new WorkItemBuilder()
                .WithId(10_530)
                .WithType("Issue")
                .WithTitle("contract")
                .WithField("System.Description", $"{OpenTag}\nx\n{CloseTag}")
                .Build());

        var cmd = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(() => cmd.Extract(10_530, fx.PolicyPath));

        output.ShouldContain("\"work_item_id\"");
        output.ShouldContain("\"source\"");
        output.ShouldContain("\"guidance\"");
        output.ShouldContain("\"guidance_present\"");
        output.ShouldNotContain("WorkItemId");
        output.ShouldNotContain("GuidancePresent");
    }
}
