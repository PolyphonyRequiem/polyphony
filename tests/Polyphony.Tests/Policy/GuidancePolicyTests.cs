using Polyphony.Policy;
using Polyphony.Sdlc;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Policy;

/// <summary>
/// Tests for the <c>guidance</c> block in <c>policy.yaml</c>: defaults,
/// explicit overrides, the ado_field opt-in invariant, and per-type
/// overlay resolution via <see cref="PolicyResolver.ResolveGuidance"/>.
/// </summary>
public sealed class GuidancePolicyTests
{
    [Fact]
    public void Load_NoGuidanceBlock_DefaultsToDescriptionBlock()
    {
        var config = PolicyLoader.Parse("schema_version: 1");
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Guidance.ShouldNotBeNull();
        config.Guidance.Source.ShouldBe(GuidanceSource.DescriptionBlock);
        config.Guidance.AdoFieldName.ShouldBeNull();
    }

    [Fact]
    public void Load_DescriptionBlockExplicit_Accepted()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: description_block
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Guidance!.Source.ShouldBe(GuidanceSource.DescriptionBlock);
    }

    [Fact]
    public void Load_AdoFieldWithFieldName_Accepted()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: ado_field
              ado_field_name: Custom.PolyphonyGuidance
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        config.Guidance!.Source.ShouldBe(GuidanceSource.AdoField);
        config.Guidance.AdoFieldName.ShouldBe("Custom.PolyphonyGuidance");
    }

    [Fact]
    public void Load_AdoFieldWithoutFieldName_ReportsLoadError()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: ado_field
            """);

        var ex = Should.Throw<InvalidOperationException>(() =>
            PolicyLoader.ApplyBuiltInDefaults(config));

        ex.Message.ShouldContain("ado_field_name");
    }

    [Fact]
    public void Load_AdoFieldWithEmptyFieldName_ReportsLoadError()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: ado_field
              ado_field_name: "  "
            """);

        Should.Throw<InvalidOperationException>(() =>
            PolicyLoader.ApplyBuiltInDefaults(config));
    }

    [Fact]
    public void Load_UnknownSource_ReportsLoadError()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: somewhere_else
            """);

        var ex = Should.Throw<InvalidOperationException>(() =>
            PolicyLoader.ApplyBuiltInDefaults(config));

        ex.Message.ShouldContain("somewhere_else");
    }

    [Fact]
    public void Load_ByTypeAdoFieldInheritsWorkspaceFieldName_Accepted()
    {
        // Type-scoped rule sets only Source; AdoFieldName inherits from defaults.
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              ado_field_name: Custom.Guidance
              by_type:
                Issue:
                  source: ado_field
            """);

        // Should not throw — the per-type rule's effective ado_field_name
        // inherits from the workspace default.
        PolicyLoader.ApplyBuiltInDefaults(config);
        config.Guidance!.ByType!.ShouldContainKey("Issue");
    }

    [Fact]
    public void Load_ByTypeAdoFieldNoFieldNameAnywhere_ReportsLoadError()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              by_type:
                Issue:
                  source: ado_field
            """);

        var ex = Should.Throw<InvalidOperationException>(() =>
            PolicyLoader.ApplyBuiltInDefaults(config));

        ex.Message.ShouldContain("guidance.by_type.Issue");
    }

    // ──────────────────────── Resolver layering ────────────────────────────

    [Fact]
    public void Resolve_DefaultScope_ReturnsWorkspaceDefaults()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: ado_field
              ado_field_name: Custom.G
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        var resolved = PolicyResolver.ResolveGuidance(config, "default");

        resolved.Source.ShouldBe(GuidanceSource.AdoField);
        resolved.AdoFieldName.ShouldBe("Custom.G");
    }

    [Fact]
    public void Resolve_RootScope_FallsThroughToDefaults()
    {
        // Guidance has no Root concept; "root" is treated as "default".
        var config = PolicyLoader.LoadOrDefault(path: "/nonexistent");
        var resolved = PolicyResolver.ResolveGuidance(config, "root");
        resolved.Source.ShouldBe(GuidanceSource.DescriptionBlock);
        resolved.AdoFieldName.ShouldBeNull();
    }

    [Fact]
    public void Resolve_TypeScope_OverlaysDefaults()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: description_block
              by_type:
                Issue:
                  source: ado_field
                  ado_field_name: Custom.IssueGuidance
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        var resolvedDefault = PolicyResolver.ResolveGuidance(config, "default");
        resolvedDefault.Source.ShouldBe(GuidanceSource.DescriptionBlock);
        resolvedDefault.AdoFieldName.ShouldBeNull();

        var resolvedIssue = PolicyResolver.ResolveGuidance(config, "type:Issue");
        resolvedIssue.Source.ShouldBe(GuidanceSource.AdoField);
        resolvedIssue.AdoFieldName.ShouldBe("Custom.IssueGuidance");
    }

    [Fact]
    public void Resolve_TypeScope_NoOverride_FallsThroughToDefaults()
    {
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: ado_field
              ado_field_name: Custom.G
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        var resolved = PolicyResolver.ResolveGuidance(config, "type:Task");

        resolved.Source.ShouldBe(GuidanceSource.AdoField);
        resolved.AdoFieldName.ShouldBe("Custom.G");
    }

    [Fact]
    public void Resolve_TypeScope_PartialOverride_InheritsFieldNameFromDefaults()
    {
        // Type-scoped rule overrides only AdoFieldName; Source inherits.
        var config = PolicyLoader.Parse("""
            schema_version: 1
            guidance:
              source: ado_field
              ado_field_name: Custom.Default
              by_type:
                Issue:
                  ado_field_name: Custom.IssueOnly
            """);
        PolicyLoader.ApplyBuiltInDefaults(config);

        var resolved = PolicyResolver.ResolveGuidance(config, "type:Issue");
        resolved.Source.ShouldBe(GuidanceSource.AdoField);
        resolved.AdoFieldName.ShouldBe("Custom.IssueOnly");
    }

    [Fact]
    public void Resolve_UnknownScope_Throws()
    {
        var config = PolicyLoader.LoadOrDefault(path: "/nonexistent");
        Should.Throw<ArgumentException>(() => PolicyResolver.ResolveGuidance(config, "garbage"));
    }
}
