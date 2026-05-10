using Polyphony.Sdlc;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Policy;

/// <summary>
/// Loads <c>.conductor/policy.yaml</c> into a <see cref="PolicyConfig"/> with
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
    /// Parses YAML text into a <see cref="PolicyConfig"/> WITHOUT applying built-in
    /// defaults. Used by <c>polyphony policy validate</c> to flag missing fields.
    /// </summary>
    public static PolicyConfig Parse(string yaml, string sourcePath = "<inline>")
    {
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
}
