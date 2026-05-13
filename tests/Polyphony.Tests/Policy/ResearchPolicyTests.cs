using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Policy;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Policy;

/// <summary>
/// Tests for the <c>research</c> policy domain: defaults, explicit overrides,
/// escalation cap validation, scope resolution, and the <c>policy load</c>
/// snapshot surface.
/// </summary>
public sealed class ResearchPolicyTests : Commands.CommandTestBase
{
    // ─────────────────────────────────────────────────────────────────────────
    // Loader defaults
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoResearchBlock_DefaultsToWarningModeAndCap1()
    {
        var config = PolicyLoader.Parse("schema_version: 1");
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Research.ShouldNotBeNull();
        config.Research.Defaults.ShouldNotBeNull();
        config.Research.Defaults!.Mode.ShouldBe(PolicyMode.Warning);
        config.Research.Defaults.EscalationCap.ShouldBe(1);
    }

    [Fact]
    public void Load_ExplicitResearchBlock_MergesWithDefaults()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                mode: auto
                escalation_cap: 3
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Research!.Defaults!.Mode.ShouldBe(PolicyMode.Auto);
        config.Research.Defaults.EscalationCap.ShouldBe(3);
    }

    [Fact]
    public void Load_ResearchModeOnly_CapDefaultsTo1()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                mode: manual
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Research!.Defaults!.Mode.ShouldBe(PolicyMode.Manual);
        config.Research.Defaults.EscalationCap.ShouldBe(1);
    }

    [Fact]
    public void Load_ResearchCapOnly_ModeDefaultsToWarning()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                escalation_cap: 5
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Research!.Defaults!.Mode.ShouldBe(PolicyMode.Warning);
        config.Research.Defaults.EscalationCap.ShouldBe(5);
    }

    [Fact]
    public void Load_ResearchWithRootAndByType_PreservesOverrides()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                mode: warning
                escalation_cap: 1
              root:
                mode: manual
                escalation_cap: 2
              by_type:
                Epic:
                  escalation_cap: 3
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Research!.Root.ShouldNotBeNull();
        config.Research.Root!.Mode.ShouldBe(PolicyMode.Manual);
        config.Research.Root.EscalationCap.ShouldBe(2);
        config.Research.ByType.ShouldNotBeNull();
        config.Research.ByType!["Epic"].EscalationCap.ShouldBe(3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Resolver (most-specific-wins)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_DefaultScope_ReturnsDefaults()
    {
        var config = LoadConfigWithResearch(mode: "warning", cap: 1);

        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Research, "default");

        resolved.Domain.ShouldBe("research");
        resolved.Scope.ShouldBe("default");
        resolved.Mode.ShouldBe("warning");
        resolved.EscalationCap.ShouldBe(1);
    }

    [Fact]
    public void Resolve_RootScope_OverridesDefaults()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                mode: warning
                escalation_cap: 1
              root:
                mode: manual
                escalation_cap: 2
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Research, "root");

        resolved.Mode.ShouldBe("manual");
        resolved.EscalationCap.ShouldBe(2);
    }

    [Fact]
    public void Resolve_TypeScope_OverridesCapOnly()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                mode: warning
                escalation_cap: 1
              by_type:
                Epic:
                  escalation_cap: 5
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Research, "type:Epic");

        resolved.Mode.ShouldBe("warning"); // inherited from defaults
        resolved.EscalationCap.ShouldBe(5); // overridden by type
    }

    [Fact]
    public void Resolve_UnknownType_FallsBackToDefaults()
    {
        var config = LoadConfigWithResearch(mode: "auto", cap: 3);

        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Research, "type:UnknownType");

        resolved.Mode.ShouldBe("auto");
        resolved.EscalationCap.ShouldBe(3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CLI: policy load (snapshot surface)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_Command_IncludesResearchSnapshot()
    {
        using var fx = new PolicyFileFixture();
        // No file — uses defaults.

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Research.ShouldNotBeNull();
        result.Research.DefaultsMode.ShouldBe("warning");
        result.Research.DefaultsEscalationCap.ShouldBe(1);
    }

    [Fact]
    public void Load_Command_ResearchOverridesReflectedInSnapshot()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                mode: auto
                escalation_cap: 4
              root:
                mode: manual
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Research.DefaultsMode.ShouldBe("auto");
        result.Research.DefaultsEscalationCap.ShouldBe(4);
        result.Research.RootMode.ShouldBe("manual");
    }

    [Fact]
    public void Load_Command_ResearchSnapshotUsesSnakeCaseJson()
    {
        using var fx = new PolicyFileFixture();
        var cmd = new PolicyCommands();
        var (_, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        output.ShouldContain("\"defaults_escalation_cap\"");
        output.ShouldNotContain("\"DefaultsEscalationCap\"");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CLI: policy resolve (research domain)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_Command_ResearchDomain_ReturnsEscalationCap()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                mode: warning
                escalation_cap: 2
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(
            () => cmd.Resolve(scope: "default", domain: "research", path: fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var resolved = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        resolved.ShouldNotBeNull();
        resolved.Domain.ShouldBe("research");
        resolved.Mode.ShouldBe("warning");
        resolved.EscalationCap.ShouldBe(2);
    }

    [Fact]
    public void Resolve_Command_ResearchDomain_RootScopeOverrides()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                mode: warning
                escalation_cap: 1
              root:
                mode: manual
                escalation_cap: 3
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(
            () => cmd.Resolve(scope: "root", domain: "research", path: fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var resolved = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        resolved.ShouldNotBeNull();
        resolved.Mode.ShouldBe("manual");
        resolved.EscalationCap.ShouldBe(3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CLI: policy validate (research-specific checks)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ResearchCapNonPositive_ReportsError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                escalation_cap: 0
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("research.defaults.escalation_cap"));
    }

    [Fact]
    public void Validate_ResearchByTypeCapNegative_ReportsError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                escalation_cap: 1
              by_type:
                Epic:
                  escalation_cap: -1
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("research.by_type.Epic.escalation_cap"));
    }

    [Fact]
    public void Validate_ResearchRootCapNonPositive_ReportsError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              root:
                escalation_cap: 0
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("research.root.escalation_cap"));
    }

    [Fact]
    public void Validate_ResearchMissingMode_WarnsAboutDefault()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                escalation_cap: 1
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("research.defaults.mode"));
    }

    [Fact]
    public void Validate_ResearchValidConfig_PassesCleanly()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                mode: warning
                escalation_cap: 1
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cross-domain: escalation_cap does NOT leak into other domains
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ApprovalsDomain_EscalationCapIsNull()
    {
        var config = LoadConfigWithResearch(mode: "warning", cap: 1);
        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Approvals, "default");

        resolved.EscalationCap.ShouldBeNull();
    }

    [Fact]
    public void Resolve_PrDomain_EscalationCapIsNull()
    {
        var config = LoadConfigWithResearch(mode: "warning", cap: 1);
        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Pr, "default");

        resolved.EscalationCap.ShouldBeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static PolicyConfig LoadConfigWithResearch(string mode, int cap)
    {
        var config = PolicyLoader.Parse($$"""
            schema_version: 1
            research:
              defaults:
                mode: {{mode}}
                escalation_cap: {{cap}}
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);
        return config;
    }
}
