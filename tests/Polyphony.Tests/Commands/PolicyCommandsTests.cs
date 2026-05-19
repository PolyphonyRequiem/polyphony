using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Policy;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for the <c>polyphony policy</c> verb group.
/// Verifies load (defaults + file), validate (missing + malformed + good),
/// and resolve (root, type, default scopes; approvals + pr domains).
/// </summary>
public sealed class PolicyCommandsTests : CommandTestBase
{
    private static PolicyCommands CreateCommand() => new();

    // ─────────────────────────────────────────────────────────────────────────
    // load
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoFile_AppliesBuiltInDefaults()
    {
        using var fx = new PolicyFileFixture();
        // No file written.

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.UsedDefaults.ShouldBeTrue();
        result.SourcePath.ShouldBeNull();
        result.SchemaVersion.ShouldBe(1);
        result.Approvals.DefaultsMode.ShouldBe("warning");
        result.Approvals.DefaultsMaxRevisionCycles.ShouldBe(5);
        result.Approvals.DefaultsQualityAvgScoreAtLeast.ShouldBe(90);
        result.Approvals.DefaultsQualityBlockingCountAtMost.ShouldBe(0);
        result.Pr.DefaultsMode.ShouldBe("warning");
        result.Pr.DefaultsMaxFixLoops.ShouldBe(10);
        result.Pr.DefaultsMaxRemediationCycles.ShouldBe(3);
        result.Concurrency.MaxConcurrentChildren.ShouldBe(3);
    }

    [Fact]
    public void Load_FileExists_MergesUserOverridesWithDefaults()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            approvals:
              defaults:
                mode: manual
                max_revision_cycles: 7
              by_type:
                Task: { mode: auto }
            pr:
              defaults:
                mode: warning
                max_fix_loops: 8
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.UsedDefaults.ShouldBeFalse();
        result.SourcePath.ShouldBe(fx.PolicyPath);
        result.Approvals.DefaultsMode.ShouldBe("manual");
        result.Approvals.DefaultsMaxRevisionCycles.ShouldBe(7);
        result.Approvals.ByTypeMode.ShouldNotBeNull();
        result.Approvals.ByTypeMode!["Task"].ShouldBe("auto");
        result.Pr.DefaultsMode.ShouldBe("warning");
        result.Pr.DefaultsMaxFixLoops.ShouldBe(8);
        // Defaults that user did NOT override
        result.Pr.DefaultsMaxRemediationCycles.ShouldBe(3);
        result.Concurrency.MaxConcurrentChildren.ShouldBe(3);
    }

    [Fact]
    public void Load_MalformedYaml_ReturnsConfigError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("not: valid: yaml: at all: [");

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("source_path").GetString().ShouldBe(fx.PolicyPath);
    }

    [Fact]
    public void Load_SnakeCaseFieldNames_PresentInRawJson()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        output.ShouldContain("\"used_defaults\"");
        output.ShouldContain("\"defaults_mode\"");
        output.ShouldContain("\"defaults_max_revision_cycles\"");
        output.ShouldContain("\"max_concurrent_children\"");
        output.ShouldNotContain("\"DefaultsMode\"");
    }

    [Fact]
    public void Load_RetiredMaxConcurrentPgsKey_ReturnsConfigErrorWithRenameGuidance()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            concurrency:
              max_concurrent_pgs: 3
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var error = doc.RootElement.GetProperty("error").GetString()!;
        error.ShouldContain("max_concurrent_pgs");
        error.ShouldContain("Polyphony 2.4.0");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // validate
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_FileMissing_ReturnsConfigErrorWithErrorField()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.Length.ShouldBe(1);
        result.Errors[0].ShouldContain("not found");
    }

    [Fact]
    public void Validate_GoodFile_ReturnsValidWithNoErrors()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            approvals:
              defaults: { mode: warning, max_revision_cycles: 5 }
            pr:
              defaults: { mode: warning, max_fix_loops: 10, max_remediation_cycles: 3 }
            concurrency:
              max_concurrent_children: 2
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        result.SchemaVersion.ShouldBe(1);
    }

    [Fact]
    public void Validate_MissingDefaultsMode_ReturnsWarning()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            approvals: {}
            pr: {}
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        // Warnings don't fail validation; valid stays true.
        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeTrue();
        result.Warnings.Length.ShouldBeGreaterThanOrEqualTo(2);
        result.Warnings.ShouldContain(w => w.Contains("approvals.defaults.mode"));
        result.Warnings.ShouldContain(w => w.Contains("pr.defaults.mode"));
    }

    [Fact]
    public void Validate_NegativeCap_ReturnsError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            approvals:
              defaults: { mode: warning, max_revision_cycles: -1 }
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("max_revision_cycles"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // resolve
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_DefaultScope_ReturnsDefaults()
    {
        using var fx = new PolicyFileFixture();

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Resolve("default", "approvals", fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Domain.ShouldBe("approvals");
        result.Scope.ShouldBe("default");
        result.Mode.ShouldBe("warning");
        result.MaxRevisionCycles.ShouldBe(5);
    }

    [Fact]
    public void Resolve_RootScope_OverridesDefaults()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            approvals:
              defaults: { mode: warning }
              root: { mode: manual }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("root", "approvals", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Mode.ShouldBe("manual");
        // Inherits caps from defaults.
        result.MaxRevisionCycles.ShouldBe(5);
    }

    [Fact]
    public void Resolve_TypeScope_OverridesDefaults()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            pr:
              defaults: { mode: warning, max_fix_loops: 10 }
              by_type:
                Issue: { mode: auto, max_fix_loops: 3 }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("type:Issue", "pr", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Mode.ShouldBe("auto");
        result.MaxFixLoops.ShouldBe(3);
    }

    [Fact]
    public void Resolve_TypeScope_FallsBackToDefaultsWhenTypeMissing()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            pr:
              defaults: { mode: warning, max_fix_loops: 10 }
              by_type:
                Task: { mode: auto }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("type:Issue", "pr", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Mode.ShouldBe("warning");
        result.MaxFixLoops.ShouldBe(10);
    }

    [Fact]
    public void Resolve_TypeScope_PartialOverride_InheritsUnsetFields()
    {
        // Acceptance criteria from plan: "by_type can override only mode while inheriting caps"
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            pr:
              defaults: { mode: warning, max_fix_loops: 10, max_remediation_cycles: 3 }
              by_type:
                Issue: { mode: auto }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("type:Issue", "pr", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Mode.ShouldBe("auto");           // from by_type
        result.MaxFixLoops.ShouldBe(10);        // inherited from defaults
        result.MaxRemediationCycles.ShouldBe(3); // inherited from defaults
    }

    [Fact]
    public void Resolve_Pr_AllowAnyApprovalVote_DefaultsToFalseWhenAbsent()
    {
        // v2.4.1: when policy.yaml omits pr.defaults.allow_any_approval_vote,
        // resolver surfaces the built-in default of false so workflows can
        // unconditionally read pr_approval_policy_ado.output.allow_any_approval_vote.
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            pr:
              defaults: { mode: warning, max_fix_loops: 10, max_remediation_cycles: 3 }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("default", "pr", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.AllowAnyApprovalVote.ShouldBe(false);
    }

    [Fact]
    public void Resolve_Pr_AllowAnyApprovalVote_RespectsDefaultsOverride()
    {
        // The dogfood scenario: cloudvault writes
        // pr.defaults.allow_any_approval_vote: true to opt into permissive
        // aggregation across all PR kinds in its repo.
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            pr:
              defaults:
                mode: warning
                max_fix_loops: 10
                max_remediation_cycles: 3
                allow_any_approval_vote: true
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("default", "pr", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.AllowAnyApprovalVote.ShouldBe(true);
    }

    [Fact]
    public void Resolve_UnknownDomain_ReturnsConfigError()
    {
        using var fx = new PolicyFileFixture();

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Resolve("default", "bogus", fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var err = doc.RootElement.GetProperty("error").GetString();
        err.ShouldNotBeNull();
        err.ShouldContain("bogus");
    }

    [Fact]
    public void Resolve_UnknownScope_ReturnsConfigError()
    {
        using var fx = new PolicyFileFixture();

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Resolve("everywhere", "approvals", fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var err = doc.RootElement.GetProperty("error").GetString();
        err.ShouldNotBeNull();
        err.ShouldContain("everywhere");
    }

    [Fact]
    public void Resolve_AllAcceptanceCriteriaFromPlanMd()
    {
        // 1. pr.by_type.Task: { mode: auto } → Impl PR resolves to mode=auto
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            approvals:
              root: { mode: manual }
            pr:
              by_type:
                Task: { mode: auto }
            """);

        var cmd = CreateCommand();
        var (_, implPrOutput) = CaptureConsole(() => cmd.Resolve("type:Task", "pr", fx.PolicyPath));
        var implPr = JsonSerializer.Deserialize(implPrOutput, PolyphonyJsonContext.Default.ResolvedRule);
        implPr.ShouldNotBeNull();
        implPr.Mode.ShouldBe("auto");

        // 2. approvals.root.mode: manual → root approvals stays manual
        var (_, rootApprovalOutput) = CaptureConsole(() => cmd.Resolve("root", "approvals", fx.PolicyPath));
        var rootApproval = JsonSerializer.Deserialize(rootApprovalOutput, PolyphonyJsonContext.Default.ResolvedRule);
        rootApproval.ShouldNotBeNull();
        rootApproval.Mode.ShouldBe("manual");

        // 3. Default Issue approval falls through to defaults (warning per built-in)
        var (_, issueOutput) = CaptureConsole(() => cmd.Resolve("type:Issue", "approvals", fx.PolicyPath));
        var issue = JsonSerializer.Deserialize(issueOutput, PolyphonyJsonContext.Default.ResolvedRule);
        issue.ShouldNotBeNull();
        issue.Mode.ShouldBe("warning");
    }

    [Fact]
    public void Resolve_SnakeCaseFieldNames()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("default", "approvals", fx.PolicyPath));

        output.ShouldContain("\"domain\"");
        output.ShouldContain("\"scope\"");
        output.ShouldContain("\"mode\"");
        output.ShouldContain("\"max_revision_cycles\"");
        output.ShouldContain("\"quality_avg_score_at_least\"");
        output.ShouldNotContain("\"MaxRevisionCycles\"");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // open_questions domain
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoFile_AppliesOpenQuestionsDefaults()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.OpenQuestions.DefaultsMode.ShouldBe("warning");
        result.OpenQuestions.DefaultsMinSeverity.ShouldBe("moderate");
        result.OpenQuestions.DefaultsMaxQuestionLoops.ShouldBe(3);
    }

    [Fact]
    public void Load_FileWithOpenQuestions_MergesOverrides()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            open_questions:
              defaults:
                mode: auto
                min_severity: critical
                max_question_loops: 5
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.OpenQuestions.DefaultsMode.ShouldBe("auto");
        result.OpenQuestions.DefaultsMinSeverity.ShouldBe("critical");
        result.OpenQuestions.DefaultsMaxQuestionLoops.ShouldBe(5);
    }

    [Fact]
    public void Resolve_OpenQuestions_DefaultScope_ReturnsDefaults()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Resolve("default", "open_questions", fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Domain.ShouldBe("open_questions");
        result.Scope.ShouldBe("default");
        result.Mode.ShouldBe("warning");
        result.MinSeverity.ShouldBe("moderate");
        result.SeveritiesAtOrAbove.ShouldBe(new[] { "moderate", "major", "critical" });
        result.MaxQuestionLoops.ShouldBe(3);
    }

    [Fact]
    public void Resolve_OpenQuestions_RootScope_OverridesDefaults()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            open_questions:
              defaults: { mode: warning, min_severity: moderate, max_question_loops: 3 }
              root: { mode: manual, min_severity: critical }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("root", "open_questions", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Mode.ShouldBe("manual");
        result.MinSeverity.ShouldBe("critical");
        // SeveritiesAtOrAbove tracks the resolved MinSeverity, not the defaults.
        result.SeveritiesAtOrAbove.ShouldBe(new[] { "critical" });
        // Inherits from defaults
        result.MaxQuestionLoops.ShouldBe(3);
    }

    [Fact]
    public void Resolve_OpenQuestions_ByTypeScope_OverridesDefaults()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            open_questions:
              defaults: { mode: warning, min_severity: moderate, max_question_loops: 3 }
              by_type:
                Issue: { mode: auto, max_question_loops: 5 }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("type:Issue", "open_questions", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Mode.ShouldBe("auto");
        result.MaxQuestionLoops.ShouldBe(5);
        // Inherits min_severity from defaults
        result.MinSeverity.ShouldBe("moderate");
    }

    [Fact]
    public void Resolve_OpenQuestions_ByTypeScope_FallsBackWhenTypeMissing()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            open_questions:
              defaults: { mode: warning, min_severity: low, max_question_loops: 2 }
              by_type:
                Task: { mode: auto }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("type:Issue", "open_questions", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Mode.ShouldBe("warning");
        result.MinSeverity.ShouldBe("low");
        // Lowest threshold → all severities included, in ascending order.
        result.SeveritiesAtOrAbove.ShouldBe(new[] { "low", "moderate", "major", "critical" });
        result.MaxQuestionLoops.ShouldBe(2);
    }

    [Fact]
    public void Resolve_OpenQuestions_SeveritiesAtOrAbove_IsAscending()
    {
        // Bug #7 (2026-05-08): plan-level.yaml referenced a Jinja function
        // `severities_at_or_above(...)` that conductor never honored; surfaced
        // live in the #3043 dogfood. Fix is a precomputed array on the policy
        // resolve output, consumed directly by route/template Jinja. Ordering
        // is fixed at ascending so workflow comparisons are deterministic.
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            open_questions:
              defaults: { mode: warning, min_severity: major, max_question_loops: 1 }
            """);

        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("default", "open_questions", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.MinSeverity.ShouldBe("major");
        result.SeveritiesAtOrAbove.ShouldBe(new[] { "major", "critical" });

        // Field name on the wire matches the workflow Jinja reference.
        output.ShouldContain("\"severities_at_or_above\":[\"major\",\"critical\"]");
    }

    [Fact]
    public void Validate_NegativeMaxQuestionLoops_ReturnsError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            open_questions:
              defaults: { mode: warning, max_question_loops: -1 }
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("max_question_loops"));
    }

    [Fact]
    public void Validate_InvalidMinSeverity_ReturnsParseError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            open_questions:
              defaults: { mode: warning, min_severity: bogus }
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Resolve_ApprovalsShape_UnchangedByOpenQuestionsAddition()
    {
        // Regression: approvals/pr JSON shape must not include open_questions fields when null.
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("default", "approvals", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Domain.ShouldBe("approvals");
        result.MinSeverity.ShouldBeNull();
        result.SeveritiesAtOrAbove.ShouldBeNull();
        result.MaxQuestionLoops.ShouldBeNull();

        // Raw JSON should not contain these fields (null fields are omitted).
        output.ShouldNotContain("\"min_severity\"");
        output.ShouldNotContain("\"severities_at_or_above\"");
        output.ShouldNotContain("\"max_question_loops\"");
    }

    [Fact]
    public void Resolve_PrShape_UnchangedByOpenQuestionsAddition()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Resolve("default", "pr", fx.PolicyPath));

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResolvedRule);
        result.ShouldNotBeNull();
        result.Domain.ShouldBe("pr");
        result.MinSeverity.ShouldBeNull();
        result.SeveritiesAtOrAbove.ShouldBeNull();
        result.MaxQuestionLoops.ShouldBeNull();

        output.ShouldNotContain("\"min_severity\"");
        output.ShouldNotContain("\"severities_at_or_above\"");
        output.ShouldNotContain("\"max_question_loops\"");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // root_fallback (Phase 1 root-fallback-gate)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoFile_AppliesRootFallbackDefaultPrompt()
    {
        using var fx = new PolicyFileFixture();

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.RootFallback.ShouldNotBeNull();
        result.RootFallback.AutoDecide.ShouldBe("prompt");
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("use_active_item")]
    [InlineData("abort")]
    public void Load_FileWithRootFallback_PreservesAutoDecide(string autoDecide)
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy($$"""
            schema_version: 1
            root_fallback:
              auto_decide: {{autoDecide}}
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.RootFallback.AutoDecide.ShouldBe(autoDecide);
    }

    [Fact]
    public void Load_BadAutoDecide_ReturnsConfigError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            root_fallback:
              auto_decide: vibes_only
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var err = doc.RootElement.GetProperty("error").GetString();
        err.ShouldNotBeNull();
        err.ShouldContain("vibes_only");
        err.ShouldContain("root_fallback.auto_decide");
    }

    [Fact]
    public void Validate_GoodRootFallback_ReturnsValid()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            approvals:
              defaults: { mode: warning, max_revision_cycles: 5 }
            pr:
              defaults: { mode: warning, max_fix_loops: 10, max_remediation_cycles: 3 }
            root_fallback:
              auto_decide: use_active_item
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_BadAutoDecide_ReturnsErrorViaParse()
    {
        // Loader-level validation kicks in inside Parse → ApplyBuiltInDefaults
        // for invalid auto_decide values; surface as a config error.
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            root_fallback:
              auto_decide: yolo
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.Length.ShouldBeGreaterThan(0);
        result.Errors.ShouldContain(e => e.Contains("yolo") || e.Contains("auto_decide"));
    }

    [Fact]
    public void Load_RootFallback_SnakeCaseFieldNames_PresentInRawJson()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        output.ShouldContain("\"root_fallback\"");
        output.ShouldContain("\"auto_decide\"");
        output.ShouldNotContain("\"RootFallback\"");
        output.ShouldNotContain("\"AutoDecide\"");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // renegotiation (Phase 7 apex-driver)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoFile_AppliesRenegotiationDefaultPrompt()
    {
        using var fx = new PolicyFileFixture();

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Renegotiation.ShouldNotBeNull();
        result.Renegotiation.AutoDecide.ShouldBe("prompt");
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("auto_restart")]
    [InlineData("ignore")]
    public void Load_FileWithRenegotiation_PreservesAutoDecide(string autoDecide)
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy($$"""
            schema_version: 1
            renegotiation:
              auto_decide: {{autoDecide}}
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Renegotiation.AutoDecide.ShouldBe(autoDecide);
    }

    [Fact]
    public void Load_BadRenegotiationAutoDecide_ReturnsConfigError()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            renegotiation:
              auto_decide: vibes_only
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var err = doc.RootElement.GetProperty("error").GetString();
        err.ShouldNotBeNull();
        err.ShouldContain("vibes_only");
        err.ShouldContain("renegotiation.auto_decide");
    }

    [Fact]
    public void Validate_GoodRenegotiation_ReturnsValid()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            approvals:
              defaults: { mode: warning, max_revision_cycles: 5 }
            pr:
              defaults: { mode: warning, max_fix_loops: 10, max_remediation_cycles: 3 }
            renegotiation:
              auto_decide: auto_restart
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_BadRenegotiationAutoDecide_ReturnsErrorViaParse()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            renegotiation:
              auto_decide: yolo
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Validate(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyValidateResult);
        result.ShouldNotBeNull();
        result.Valid.ShouldBeFalse();
        result.Errors.Length.ShouldBeGreaterThan(0);
        result.Errors.ShouldContain(e => e.Contains("yolo") || e.Contains("auto_decide"));
    }

    [Fact]
    public void Load_Renegotiation_SnakeCaseFieldNames_PresentInRawJson()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        // Note: Shouldly's `ShouldNotContain` is case-insensitive — we cannot assert
        // the absence of a PascalCase variant when the snake_case form is itself a
        // case-insensitive substring (no underscore to differentiate them). Positive
        // assertion suffices: the JSON property must be the snake_case literal.
        output.ShouldContain("\"renegotiation\"");
        output.ShouldContain("\"auto_decide\"");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // unattended (AB#3104 — Bucket-C policy controllability)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_NoFile_AppliesUnattendedDefaults()
    {
        using var fx = new PolicyFileFixture();

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Unattended.ShouldNotBeNull();
        result.Unattended.AcceptanceMode.ShouldBe("manual");
        result.Unattended.ReviewWaitMode.ShouldBe("wait");
        result.Unattended.CapMode.ShouldBe("manual");
    }

    [Theory]
    [InlineData("manual", "wait", "manual")]
    [InlineData("auto", "skip", "auto_proceed")]
    [InlineData("auto", "wait", "auto_fail")]
    [InlineData("manual", "skip", "auto_proceed")]
    public void Load_FileWithUnattended_PreservesAllModes(
        string acceptance, string reviewWait, string cap)
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy($$"""
            schema_version: 1
            unattended:
              acceptance_mode: {{acceptance}}
              review_wait_mode: {{reviewWait}}
              cap_mode: {{cap}}
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Unattended.AcceptanceMode.ShouldBe(acceptance);
        result.Unattended.ReviewWaitMode.ShouldBe(reviewWait);
        result.Unattended.CapMode.ShouldBe(cap);
    }

    [Fact]
    public void Load_ReviewWaitModeAuto_ReturnsConfigErrorReferencingIssue444()
    {
        // Issue #444: `review_wait_mode: auto` previously triggered a
        // magic-comment self-approve path that was removed when the
        // sentiment-driven PR-review loop landed (PRs #438 / #440). The
        // loader rejects it now so the misconfiguration surfaces at
        // validate-config time instead of mid-apex via the workflow
        // router's terminal_abort_auto_mode_unsupported route.
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            unattended:
              review_wait_mode: auto
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var err = doc.RootElement.GetProperty("error").GetString();
        err.ShouldNotBeNull();
        err.ShouldContain("review_wait_mode");
        err.ShouldContain("auto");
        err.ShouldContain("#444");
        err.ShouldContain("skip");
        err.ShouldContain("wait");
    }

    [Fact]
    public void Load_PartialUnattended_FillsMissingFromDefaults()
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy("""
            schema_version: 1
            unattended:
              acceptance_mode: auto
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PolicyLoadResult);
        result.ShouldNotBeNull();
        result.Unattended.AcceptanceMode.ShouldBe("auto");
        result.Unattended.ReviewWaitMode.ShouldBe("wait");
        result.Unattended.CapMode.ShouldBe("manual");
    }

    [Theory]
    [InlineData("acceptance_mode: vibes", "vibes", "acceptance_mode")]
    [InlineData("review_wait_mode: maybe", "maybe", "review_wait_mode")]
    [InlineData("cap_mode: explode", "explode", "cap_mode")]
    public void Load_BadUnattendedMode_ReturnsConfigError(
        string badLine, string badToken, string fieldName)
    {
        using var fx = new PolicyFileFixture();
        fx.WritePolicy($$"""
            schema_version: 1
            unattended:
              {{badLine}}
            """);

        var cmd = CreateCommand();
        var (exitCode, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var doc = JsonDocument.Parse(output);
        var err = doc.RootElement.GetProperty("error").GetString();
        err.ShouldNotBeNull();
        err.ShouldContain(badToken);
        err.ShouldContain(fieldName);
    }

    [Fact]
    public void Load_Unattended_SnakeCaseFieldNames_PresentInRawJson()
    {
        using var fx = new PolicyFileFixture();
        var cmd = CreateCommand();
        var (_, output) = CaptureConsole(() => cmd.Load(fx.PolicyPath));

        output.ShouldContain("\"unattended\"");
        output.ShouldContain("\"acceptance_mode\"");
        output.ShouldContain("\"review_wait_mode\"");
        output.ShouldContain("\"cap_mode\"");
    }
}

/// <summary>
/// Disposable fixture for a temp-dir <c>policy.yaml</c>.
/// </summary>
internal sealed class PolicyFileFixture : IDisposable
{
    public string Dir { get; }
    public string PolicyPath { get; }

    public PolicyFileFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), $"polyphony-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Dir);
        PolicyPath = Path.Combine(Dir, "policy.yaml");
    }

    public void WritePolicy(string yaml) => File.WriteAllText(PolicyPath, yaml);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Dir))
                Directory.Delete(Dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
