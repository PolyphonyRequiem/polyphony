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
    // MaxResearchLoops (research-loop-policy)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoResearchBlock_MaxResearchLoopsDefaultsTo3()
    {
        var config = PolicyLoader.Parse("schema_version: 1");
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Research!.Defaults!.MaxResearchLoops.ShouldBe(3);
    }

    [Fact]
    public void Load_ExplicitMaxResearchLoops_OverridesDefault()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                max_research_loops: 5
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Research!.Defaults!.MaxResearchLoops.ShouldBe(5);
    }

    [Fact]
    public void Load_MaxResearchLoopsRootAndByType_PreservesOverrides()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                max_research_loops: 3
              root:
                max_research_loops: 7
              by_type:
                Epic:
                  max_research_loops: 10
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Research!.Root!.MaxResearchLoops.ShouldBe(7);
        config.Research.ByType!["Epic"].MaxResearchLoops.ShouldBe(10);
    }

    [Fact]
    public void Resolve_ResearchDomain_DefaultScope_SurfacesMaxResearchLoops()
    {
        var config = LoadConfigWithResearchAndLoops(mode: "warning", cap: 1, maxLoops: 4);
        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Research, "default");

        resolved.MaxResearchLoops.ShouldBe(4);
    }

    [Fact]
    public void Resolve_ResearchDomain_RootOverridesMaxResearchLoops()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                max_research_loops: 3
              root:
                max_research_loops: 8
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);
        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Research, "root");

        resolved.MaxResearchLoops.ShouldBe(8);
    }

    [Fact]
    public void Resolve_ResearchDomain_ByTypeOverridesMaxResearchLoops()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            research:
              defaults:
                max_research_loops: 3
              by_type:
                Epic:
                  max_research_loops: 10
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);
        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Research, "type:Epic");

        resolved.MaxResearchLoops.ShouldBe(10);
    }

    [Fact]
    public void Resolve_ApprovalsDomain_MaxResearchLoopsIsNull()
    {
        var config = LoadConfigWithResearchAndLoops(mode: "warning", cap: 1, maxLoops: 3);
        var resolved = PolicyResolver.Resolve(config, PolicyDomain.Approvals, "default");

        resolved.MaxResearchLoops.ShouldBeNull();
    }

    [Fact]
    public void Load_Command_MaxResearchLoopsReflectedInSnapshot()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                max_research_loops: 7
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Research.DefaultsMaxResearchLoops.ShouldBe(7);
    }

    [Fact]
    public void Load_Command_MaxResearchLoopsDefault_ReflectedInSnapshot()
    {
        using var fx = new PolicyFileFixture();
        // No file — uses defaults.
        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Research.DefaultsMaxResearchLoops.ShouldBe(3);
    }

    [Fact]
    public void Resolve_Command_ResearchDomain_SurfacesMaxResearchLoops()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                max_research_loops: 6
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(
            () => cmd.Resolve(scope: "default", domain: "research", path: fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        // Use raw JSON check — ResolvedRule's MaxResearchLoops has snake_case
        // JsonPropertyName so the script consuming the envelope can rely on it.
        output.ShouldContain("\"max_research_loops\":6");
    }

    [Fact]
    public void Validate_MaxResearchLoopsNegative_ReportsError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                max_research_loops: -1
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("research.defaults.max_research_loops"));
    }

    [Fact]
    public void Validate_MaxResearchLoopsZero_Accepts()
    {
        // 0 disables research entirely — legal per the policy contract.
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              defaults:
                mode: warning
                max_research_loops: 0
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_MaxResearchLoopsRootNegative_ReportsError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              root:
                max_research_loops: -5
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("research.root.max_research_loops"));
    }

    [Fact]
    public void Validate_MaxResearchLoopsByTypeNegative_ReportsError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            research:
              by_type:
                Epic:
                  max_research_loops: -10
            """);

        var cmd = new PolicyCommands();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("research.by_type.Epic.max_research_loops"));
    }

    private static PolicyConfig LoadConfigWithResearchAndLoops(string mode, int cap, int maxLoops)
    {
        var config = PolicyLoader.Parse($$"""
            schema_version: 1
            research:
              defaults:
                mode: {{mode}}
                escalation_cap: {{cap}}
                max_research_loops: {{maxLoops}}
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);
        return config;
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
