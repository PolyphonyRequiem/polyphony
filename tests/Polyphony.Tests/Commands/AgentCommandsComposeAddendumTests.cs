using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Models;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony agent compose-addendum</c>. Verifies
/// the routing-style envelope (always exit 0; <c>error_code</c> categorical
/// for failures), the union/dedup/sort semantics inherited from
/// <see cref="Sdlc.FacetProfileComposer"/>, the per-item guidance pass-through,
/// and the snake_case JSON contract enforced by
/// <see cref="PolyphonyJsonContext"/>.
/// </summary>
public sealed class AgentCommandsComposeAddendumTests : CommandTestBase
{
    private const string OpenTag = "<!-- polyphony:guidance -->";
    private const string CloseTag = "<!-- /polyphony:guidance -->";

    private AgentCommands CreateCommand(Polyphony.Configuration.ProcessConfig? config = null) =>
        new(Repository, config ?? Config);

    [Fact]
    public async Task Compose_AllFacetsBound_UnionsAndSortsSkillsAndMcps()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Story", ["actionable", "implementable"], null)
            .WithFacetProfile("actionable",
                skills: ["actionable-evidence", "telemetry"],
                mcps: ["telemetry-mcp"])
            .WithFacetProfile("implementable",
                skills: ["build", "test"],
                mcps: ["git", "shell"])
            .WithBranchStrategy()
            .Build();

        await SeedAsync(new WorkItemBuilder()
            .WithId(20_001).WithType("Story").WithTitle("Has both facets").Build());

        var policyFx = new PolicyFileFixture();
        try
        {
            var cmd = CreateCommand(config);
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(20_001, policyFx.PolicyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.AgentComposeAddendumResult);
            result.ShouldNotBeNull();
            result.WorkItemId.ShouldBe(20_001);
            result.Facets.ShouldBe(new[] { "actionable", "implementable" });
            // Sorted ordinal ascending across both facets.
            result.Skills.ShouldBe(new[] { "actionable-evidence", "build", "telemetry", "test" });
            result.Mcps.ShouldBe(new[] { "git", "shell", "telemetry-mcp" });
            result.GuidancePresent.ShouldBeFalse();
            result.Guidance.ShouldBeNull();
            result.Error.ShouldBeNull();
            result.ErrorCode.ShouldBeNull();
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_IdenticalCrossFacetEntries_DedupedSilently()
    {
        // The composer's union/dedup contract: a skill bound by two
        // facets ends up in the addendum once.
        var config = new ProcessConfigBuilder()
            .WithType("Story", ["actionable", "implementable"], null)
            .WithFacetProfile("actionable", skills: ["evidence"], mcps: ["shell"])
            .WithFacetProfile("implementable", skills: ["evidence"], mcps: ["shell"])
            .WithBranchStrategy()
            .Build();

        await SeedAsync(new WorkItemBuilder()
            .WithId(20_002).WithType("Story").WithTitle("Cross-facet duplicates").Build());

        var policyFx = new PolicyFileFixture();
        try
        {
            var cmd = CreateCommand(config);
            var (_, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(20_002, policyFx.PolicyPath));

            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.AgentComposeAddendumResult);
            result!.Skills.ShouldBe(new[] { "evidence" });
            result.Mcps.ShouldBe(new[] { "shell" });
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_FacetWithoutBoundProfile_SilentlySkipped()
    {
        // The item carries 'actionable' + 'implementable' but only
        // 'actionable' has a profile. The composer is permissive at
        // compose-time — no facet_unknown error.
        var config = new ProcessConfigBuilder()
            .WithType("Story", ["actionable", "implementable"], null)
            .WithFacetProfile("actionable", skills: ["evidence"], mcps: ["shell"])
            .WithBranchStrategy()
            .Build();

        await SeedAsync(new WorkItemBuilder()
            .WithId(20_003).WithType("Story").WithTitle("Partial profile coverage").Build());

        var policyFx = new PolicyFileFixture();
        try
        {
            var cmd = CreateCommand(config);
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(20_003, policyFx.PolicyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.AgentComposeAddendumResult);
            result!.Facets.ShouldBe(new[] { "actionable", "implementable" });
            result.Skills.ShouldBe(new[] { "evidence" });
            result.Mcps.ShouldBe(new[] { "shell" });
            result.Error.ShouldBeNull();
            result.ErrorCode.ShouldBeNull();
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_NoFacetsBlock_EmptySkillsAndMcps()
    {
        // No top-level `facets:` block on the process config — composer
        // returns empty addendum, verb still succeeds.
        var config = new ProcessConfigBuilder()
            .WithType("Story", ["actionable"], null)
            .WithBranchStrategy()
            .Build();

        await SeedAsync(new WorkItemBuilder()
            .WithId(20_004).WithType("Story").WithTitle("No bindings").Build());

        var policyFx = new PolicyFileFixture();
        try
        {
            var cmd = CreateCommand(config);
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(20_004, policyFx.PolicyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.AgentComposeAddendumResult);
            result!.Facets.ShouldBe(new[] { "actionable" });
            result.Skills.ShouldBeEmpty();
            result.Mcps.ShouldBeEmpty();
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_GuidanceFromDescriptionBlock_PinnedOnEnvelope()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Story", ["implementable"], null)
            .WithFacetProfile("implementable", skills: ["build"], mcps: ["git"])
            .WithBranchStrategy()
            .Build();

        await SeedAsync(new WorkItemBuilder()
            .WithId(20_010).WithType("Story").WithTitle("Has guidance")
            .WithField("System.Description",
                $"intro\n{OpenTag}\nUse Polly for retries.\n{CloseTag}\noutro")
            .Build());

        var policyFx = new PolicyFileFixture();
        try
        {
            // Default policy = description_block.
            var cmd = CreateCommand(config);
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(20_010, policyFx.PolicyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.AgentComposeAddendumResult);
            result!.Guidance.ShouldBe("Use Polly for retries.");
            result.GuidancePresent.ShouldBeTrue();
            result.Skills.ShouldBe(new[] { "build" });
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_WorkItemNotFound_RoutingEnvelope_WithSuccessExitCode()
    {
        var policyFx = new PolicyFileFixture();
        try
        {
            var cmd = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(99_900, policyFx.PolicyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var doc = JsonDocument.Parse(output);
            doc.RootElement.GetProperty("error_code").GetString().ShouldBe("work_item_not_found");
            doc.RootElement.GetProperty("error").GetString()!.ShouldContain("99900");
            doc.RootElement.GetProperty("work_item_id").GetInt32().ShouldBe(99_900);
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_TypeUnknownToProcessConfig_RoutingEnvelope()
    {
        // Item exists but its type is not declared in the process config.
        await SeedAsync(new WorkItemBuilder()
            .WithId(20_020).WithType("Mystery").WithTitle("Unknown type").Build());

        var policyFx = new PolicyFileFixture();
        try
        {
            var cmd = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(20_020, policyFx.PolicyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var doc = JsonDocument.Parse(output);
            doc.RootElement.GetProperty("error_code").GetString().ShouldBe("type_unknown");
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_GuidanceMisconfigured_AdoFieldWithoutFieldName_RoutingEnvelope()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Story", ["implementable"], null)
            .WithBranchStrategy()
            .Build();

        await SeedAsync(new WorkItemBuilder()
            .WithId(20_030).WithType("Story").WithTitle("Guidance misconfig").Build());

        var policyFx = new PolicyFileFixture();
        try
        {
            policyFx.WritePolicy("""
                schema_version: 1
                guidance:
                  source: ado_field
                """);

            var cmd = CreateCommand(config);
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(20_030, policyFx.PolicyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var doc = JsonDocument.Parse(output);
            doc.RootElement.GetProperty("error_code").GetString().ShouldBe("guidance_misconfigured");
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_InvalidWorkItemId_RoutingEnvelope()
    {
        var policyFx = new PolicyFileFixture();
        try
        {
            var cmd = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(0, policyFx.PolicyPath));

            exitCode.ShouldBe(ExitCodes.Success);
            var doc = JsonDocument.Parse(output);
            doc.RootElement.GetProperty("error_code").GetString().ShouldBe("invalid_argument");
        }
        finally { policyFx.Dispose(); }
    }

    [Fact]
    public async Task Compose_JsonContract_SnakeCaseKeys_NoPascalCaseLeakage()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Story", ["actionable"], null)
            .WithFacetProfile("actionable", skills: ["evidence"], mcps: ["shell"])
            .WithBranchStrategy()
            .Build();

        await SeedAsync(new WorkItemBuilder()
            .WithId(20_040).WithType("Story").WithTitle("contract").Build());

        var policyFx = new PolicyFileFixture();
        try
        {
            var cmd = CreateCommand(config);
            var (_, output) = await CaptureConsoleAsync(
                () => cmd.ComposeAddendum(20_040, policyFx.PolicyPath));

            output.ShouldContain("\"work_item_id\"");
            output.ShouldContain("\"facets\"");
            output.ShouldContain("\"skills\"");
            output.ShouldContain("\"mcps\"");
            output.ShouldContain("\"guidance_present\"");
            output.ShouldNotContain("WorkItemId");
            output.ShouldNotContain("GuidancePresent");
            output.ShouldNotContain("ErrorCode");
            // Null fields omitted (WhenWritingNull).
            output.ShouldNotContain("\"guidance\":null");
            output.ShouldNotContain("\"error\":null");
            output.ShouldNotContain("\"error_code\":null");
        }
        finally { policyFx.Dispose(); }
    }
}
