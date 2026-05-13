using Polyphony.Sdlc;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Policy;

/// <summary>
/// Loads <c>.polyphony-config/policy.yaml</c> into a <see cref="PolicyConfig"/> with
/// sensible built-in defaults applied when a file is missing or partial.
/// Defaults match the plan-of-record:
/// <list type="bullet">
///   <item><description>approvals.defaults.mode = warning</description></item>
///   <item><description>approvals.defaults.max_revision_cycles = 5</description></item>
///   <item><description>approvals.defaults.quality_threshold.avg_score_at_least = 90</description></item>
///   <item><description>approvals.defaults.quality_threshold.blocking_count_at_most = 0</description></item>
///   <item><description>pr.defaults.mode = warning</description></item>
///   <item><description>pr.defaults.max_fix_loops = 10</description></item>
///   <item><description>pr.defaults.max_remediation_cycles = 3</description></item>
///   <item><description>open_questions.defaults.mode = warning</description></item>
///   <item><description>open_questions.defaults.min_severity = moderate</description></item>
///   <item><description>open_questions.defaults.max_question_loops = 3</description></item>
///   <item><description>concurrency.max_concurrent_children = 3</description></item>
///   <item><description>guidance.source = description_block</description></item>
///   <item><description>guidance.ado_field_name = null</description></item>
///   <item><description>root_fallback.auto_decide = prompt</description></item>
///   <item><description>renegotiation.auto_decide = prompt</description></item>
/// </list>
///
/// Also enforces a load-time invariant: when <c>guidance.source</c> is
/// <c>ado_field</c>, <c>guidance.ado_field_name</c> must be non-empty,
/// <c>root_fallback.auto_decide</c> must be one of <c>prompt</c>,
/// <c>use_active_item</c>, or <c>abort</c> when set, and
/// <c>renegotiation.auto_decide</c> must be one of <c>prompt</c>,
/// <c>auto_restart</c>, or <c>ignore</c> when set.
/// </summary>
public static class PolicyLoader
{
    /// <summary>
    /// Canonical default policy file path, relative to the repo/worktree root.
    /// Centralised so CLI verbs and the env-var resolver agree on what
    /// "the default" means.
    /// </summary>
    public const string DefaultPath = ".polyphony-config/policy.yaml";

    /// <summary>
    /// Environment variable that overrides the default policy path when no
    /// explicit non-default path is supplied by a caller. Lets an operator
    /// switch policies for an entire shell session (e.g. fast-track) without
    /// editing every CLI invocation.
    /// </summary>
    public const string PathEnvVar = "POLYPHONY_POLICY_PATH";

    /// <summary>
    /// Resolves the effective policy path with precedence:
    /// <list type="number">
    ///   <item>Explicit non-default <paramref name="explicitPath"/> wins (CLI <c>--path other.yaml</c>, programmatic).</item>
    ///   <item>Otherwise, <see cref="PathEnvVar"/> (when set and non-whitespace).</item>
    ///   <item>Otherwise, <see cref="DefaultPath"/>.</item>
    /// </list>
    /// Note: passing the literal default path is treated as "no explicit override",
    /// so the env var still wins. This matches the common CLI shape where
    /// <c>--path</c> defaults to <see cref="DefaultPath"/> and we cannot tell whether
    /// the operator typed it or accepted the default.
    /// </summary>
    public static string ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath) && explicitPath != DefaultPath)
            return explicitPath;

        var fromEnv = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        return DefaultPath;
    }

    /// <summary>
    /// Loads a policy config from <paramref name="path"/>. When the file does not
    /// exist, returns a fully-defaulted config (no exception). When the file exists
    /// but parses cleanly, defaults are merged into any missing fields so callers
    /// always see a complete config.
    /// </summary>
    /// <exception cref="InvalidOperationException">YAML is malformed or unsupported schema.</exception>
    public static PolicyConfig LoadOrDefault(string path)
    {
        var config = File.Exists(path) ? Parse(File.ReadAllText(path), path) : new PolicyConfig();
        ApplyBuiltInDefaults(config);
        return config;
    }

    /// <summary>
    /// Convenience wrapper: <see cref="ResolvePath"/> followed by <see cref="LoadOrDefault"/>.
    /// CLI verbs and other env-var-aware callers use this instead of
    /// <see cref="LoadOrDefault"/> so the <see cref="PathEnvVar"/> override is honoured.
    /// </summary>
    public static PolicyConfig LoadOrDefaultResolved(string? explicitPath = null)
        => LoadOrDefault(ResolvePath(explicitPath));

    /// <summary>
    /// Parses YAML text into a <see cref="PolicyConfig"/> WITHOUT applying built-in
    /// defaults. Used by <c>polyphony policy validate</c> to flag missing fields.
    /// </summary>
    public static PolicyConfig Parse(string yaml, string sourcePath = "<inline>")
    {
        RejectRetiredKeys(yaml, sourcePath);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        try
        {
            var config = deserializer.Deserialize<PolicyConfig>(yaml)
                ?? throw new InvalidOperationException($"Empty policy config: {sourcePath}");

            if (config.SchemaVersion > 1)
                throw new InvalidOperationException(
                    $"Unsupported policy schema version {config.SchemaVersion} in {sourcePath}. " +
                    "This version of Polyphony supports schema_version 1.");

            return config;
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"Failed to parse policy YAML at {sourcePath}: {ex.Message}", ex);
        }
    }

    private static void RejectRetiredKeys(string yaml, string sourcePath)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            yaml, @"^\s*max_concurrent_pgs\s*:",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!match.Success) return;

        var lineNumber = yaml.Take(match.Index).Count(static c => c == '\n') + 1;
        throw new InvalidOperationException(
            $"Policy config '{sourcePath}' line {lineNumber}: key 'max_concurrent_pgs' is no longer supported. " +
            "It was removed alongside the G2 PG → MergeGroup consolidation in Polyphony 2.4.0; " +
            "no replacement key is needed (the cap was unused at runtime).");
    }

    /// <summary>
    /// Merges built-in defaults into any null leaves in <paramref name="config"/>.
    /// Idempotent — safe to call repeatedly.
    /// </summary>
    public static void ApplyBuiltInDefaults(PolicyConfig config)
    {
        config.Approvals ??= new DomainPolicy();
        config.Approvals.Defaults ??= new ScopeRule();
        config.Approvals.Defaults.Mode ??= PolicyMode.Warning;
        config.Approvals.Defaults.MaxRevisionCycles ??= 5;
        config.Approvals.Defaults.QualityThreshold ??= new QualityThreshold();
        config.Approvals.Defaults.QualityThreshold.AvgScoreAtLeast ??= 90;
        config.Approvals.Defaults.QualityThreshold.BlockingCountAtMost ??= 0;

        config.Pr ??= new DomainPolicy();
        config.Pr.Defaults ??= new ScopeRule();
        config.Pr.Defaults.Mode ??= PolicyMode.Warning;
        config.Pr.Defaults.MaxFixLoops ??= 10;
        config.Pr.Defaults.MaxRemediationCycles ??= 3;

        config.OpenQuestions ??= new DomainPolicy();
        config.OpenQuestions.Defaults ??= new ScopeRule();
        config.OpenQuestions.Defaults.Mode ??= PolicyMode.Warning;
        config.OpenQuestions.Defaults.MinSeverity ??= Severity.Moderate;
        config.OpenQuestions.Defaults.MaxQuestionLoops ??= 3;

        config.Concurrency ??= new ConcurrencyPolicy();
        config.Concurrency.MaxConcurrentChildren ??= 3;

        config.Guidance ??= new GuidancePolicy();
        config.Guidance.Source ??= GuidanceSource.DescriptionBlock;

        ValidateGuidance(config.Guidance);

        config.RootFallback ??= new RootFallbackPolicy();
        config.RootFallback.AutoDecide ??= RootFallbackAutoDecide.Prompt;

        ValidateRootFallback(config.RootFallback);

        config.Renegotiation ??= new RenegotiationPolicy();
        config.Renegotiation.AutoDecide ??= RenegotiationAutoDecide.Prompt;

        ValidateRenegotiation(config.Renegotiation);

        config.Unattended ??= new UnattendedPolicy();
        config.Unattended.AcceptanceMode ??= UnattendedAcceptanceMode.Manual;
        config.Unattended.ReviewWaitMode ??= UnattendedReviewWaitMode.Wait;
        config.Unattended.CapMode ??= UnattendedCapMode.Manual;

        ValidateUnattended(config.Unattended);

        config.Research ??= new DomainPolicy();
        config.Research.Defaults ??= new ScopeRule();
        config.Research.Defaults.Mode ??= PolicyMode.Warning;
        config.Research.Defaults.EscalationCap ??= 1;
    }

    private static void ValidateGuidance(GuidancePolicy guidance)
    {
        ValidateGuidanceRule(
            scope: "guidance",
            source: guidance.Source,
            adoFieldName: guidance.AdoFieldName);

        if (guidance.ByType is null) return;
        foreach (var (typeName, rule) in guidance.ByType)
        {
            // Per-type rules inherit unspecified fields from the workspace default,
            // so the effective values are what we validate against.
            var effectiveSource = rule.Source ?? guidance.Source;
            var effectiveField = rule.AdoFieldName ?? guidance.AdoFieldName;
            ValidateGuidanceRule(
                scope: $"guidance.by_type.{typeName}",
                source: effectiveSource,
                adoFieldName: effectiveField);
        }
    }

    private static void ValidateGuidanceRule(string scope, string? source, string? adoFieldName)
    {
        if (source is not null && !GuidanceSource.IsValid(source))
            throw new InvalidOperationException(
                $"{scope}.source '{source}' is not a known guidance source. " +
                $"Expected '{GuidanceSource.DescriptionBlock}' or '{GuidanceSource.AdoField}'.");

        if (source == GuidanceSource.AdoField && string.IsNullOrWhiteSpace(adoFieldName))
            throw new InvalidOperationException(
                $"{scope}.source is '{GuidanceSource.AdoField}' but {scope}.ado_field_name is not set. " +
                "Set ado_field_name to the ADO custom field reference name (e.g. 'Custom.PolyphonyGuidance').");
    }

    private static void ValidateRootFallback(RootFallbackPolicy rootFallback)
    {
        var value = rootFallback.AutoDecide;
        if (value is not null && !RootFallbackAutoDecide.IsValid(value))
            throw new InvalidOperationException(
                $"root_fallback.auto_decide '{value}' is not a known auto-decide policy. " +
                $"Expected '{RootFallbackAutoDecide.Prompt}', '{RootFallbackAutoDecide.UseActiveItem}', " +
                $"or '{RootFallbackAutoDecide.Abort}'.");
    }

    private static void ValidateRenegotiation(RenegotiationPolicy renegotiation)
    {
        var value = renegotiation.AutoDecide;
        if (value is not null && !RenegotiationAutoDecide.IsValid(value))
            throw new InvalidOperationException(
                $"renegotiation.auto_decide '{value}' is not a known auto-decide policy. " +
                $"Expected '{RenegotiationAutoDecide.Prompt}', '{RenegotiationAutoDecide.AutoRestart}', " +
                $"or '{RenegotiationAutoDecide.Ignore}'.");
    }

    private static void ValidateUnattended(UnattendedPolicy unattended)
    {
        if (unattended.AcceptanceMode is { } accept && !UnattendedAcceptanceMode.IsValid(accept))
            throw new InvalidOperationException(
                $"unattended.acceptance_mode '{accept}' is not a known mode. " +
                $"Expected '{UnattendedAcceptanceMode.Manual}' or '{UnattendedAcceptanceMode.Auto}'.");

        if (unattended.ReviewWaitMode is { } wait && !UnattendedReviewWaitMode.IsValid(wait))
            throw new InvalidOperationException(
                $"unattended.review_wait_mode '{wait}' is not a known mode. " +
                $"Expected '{UnattendedReviewWaitMode.Wait}', '{UnattendedReviewWaitMode.Skip}', " +
                $"or '{UnattendedReviewWaitMode.Auto}'.");

        if (unattended.CapMode is { } cap && !UnattendedCapMode.IsValid(cap))
            throw new InvalidOperationException(
                $"unattended.cap_mode '{cap}' is not a known mode. " +
                $"Expected '{UnattendedCapMode.Manual}', '{UnattendedCapMode.AutoProceed}', " +
                $"or '{UnattendedCapMode.AutoFail}'.");
    }
}
