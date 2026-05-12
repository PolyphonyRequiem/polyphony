using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Policy;
using Polyphony.Sdlc;

namespace Polyphony.Commands;

/// <summary>
/// Policy verbs (<c>polyphony policy ...</c>). Phase 7 of Epic 2978 — load and
/// resolve <c>.polyphony-config/policy.yaml</c> at the start of a workflow run, then
/// per-step query the effective mode + caps for each scope.
///
/// <list type="bullet">
///   <item><c>load</c> — reads policy.yaml (or defaults) and emits a snapshot for downstream consumption.</item>
///   <item><c>validate</c> — schema-validates a policy file without applying defaults; emits errors/warnings.</item>
///   <item><c>resolve</c> — returns the effective mode + caps for a given scope and domain (most-specific wins).</item>
/// </list>
/// </summary>
[VerbGroup("policy")]
public sealed class PolicyCommands
{
    /// <summary>
    /// Loads <c>.polyphony-config/policy.yaml</c> (or <paramref name="path"/> if specified)
    /// and emits a snapshot of the resolved configuration with built-in defaults
    /// applied. When the file does not exist, returns a defaults-only snapshot
    /// with <c>used_defaults: true</c>.
    /// </summary>
    /// <param name="path">Path to the policy file. Defaults to <c>.polyphony-config/policy.yaml</c>;
    /// the <c>POLYPHONY_POLICY_PATH</c> environment variable overrides the default when no
    /// explicit non-default path is supplied.</param>
    [Command("load")]
    [VerbResult(typeof(PolicyLoadResult))]
    public int Load(string path = PolicyLoader.DefaultPath)
    {
        var resolvedPath = PolicyLoader.ResolvePath(path);

        PolicyConfig config;
        try
        {
            config = PolicyLoader.LoadOrDefault(resolvedPath);
        }
        catch (Exception ex)
        {
            EmitLoadError(resolvedPath, ex.Message);
            return ExitCodes.ConfigError;
        }

        var result = new PolicyLoadResult
        {
            SchemaVersion = config.SchemaVersion,
            SourcePath = File.Exists(resolvedPath) ? resolvedPath : null,
            UsedDefaults = !File.Exists(resolvedPath),
            Approvals = SnapshotDomain(config.Approvals!),
            Pr = SnapshotDomain(config.Pr!),
            OpenQuestions = SnapshotDomain(config.OpenQuestions!),
            Concurrency = new PolicyConcurrencySnapshot
            {
                MaxConcurrentChildren = config.Concurrency!.MaxConcurrentChildren!.Value,
            },
            Guidance = SnapshotGuidance(config.Guidance!),
            RootFallback = SnapshotRootFallback(config.RootFallback!),
            Renegotiation = SnapshotRenegotiation(config.Renegotiation!),
            Unattended = SnapshotUnattended(config.Unattended!),
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PolicyLoadResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Schema-validates a policy file WITHOUT applying built-in defaults. Surfaces
    /// missing required fields and unknown enum values as warnings/errors so the
    /// operator can choose to opt into defaults explicitly.
    /// </summary>
    /// <param name="path">Path to the policy file. Defaults to <c>.polyphony-config/policy.yaml</c>;
    /// the <c>POLYPHONY_POLICY_PATH</c> environment variable overrides the default when no
    /// explicit non-default path is supplied.</param>
    [Command("validate")]
    [VerbResult(typeof(PolicyValidateResult))]
    public int Validate(string path = PolicyLoader.DefaultPath)
    {
        var resolvedPath = PolicyLoader.ResolvePath(path);

        if (!File.Exists(resolvedPath))
        {
            var missing = new PolicyValidateResult
            {
                Valid = false,
                SourcePath = resolvedPath,
                Errors = [$"Policy file not found: {resolvedPath}"],
                Warnings = [],
            };
            Console.WriteLine(JsonSerializer.Serialize(missing, PolyphonyJsonContext.Default.PolicyValidateResult));
            return ExitCodes.ConfigError;
        }

        PolicyConfig config;
        try
        {
            config = PolicyLoader.Parse(File.ReadAllText(resolvedPath), resolvedPath);
        }
        catch (Exception ex)
        {
            var failed = new PolicyValidateResult
            {
                Valid = false,
                SourcePath = resolvedPath,
                Errors = [ex.Message],
                Warnings = [],
            };
            Console.WriteLine(JsonSerializer.Serialize(failed, PolyphonyJsonContext.Default.PolicyValidateResult));
            return ExitCodes.ConfigError;
        }

        var (errors, warnings) = ValidateRules(config);

        var result = new PolicyValidateResult
        {
            Valid = errors.Count == 0,
            SourcePath = resolvedPath,
            SchemaVersion = config.SchemaVersion,
            Errors = [.. errors],
            Warnings = [.. warnings],
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PolicyValidateResult));
        return errors.Count == 0 ? ExitCodes.Success : ExitCodes.ConfigError;
    }

    /// <summary>
    /// Returns the effective rule for <paramref name="scope"/> within
    /// <paramref name="domain"/>, layering the policy file most-specific-wins
    /// (root &gt; type:&lt;Name&gt; &gt; default).
    /// </summary>
    /// <param name="scope">Scope token: <c>root</c>, <c>default</c>, or <c>type:Name</c>.</param>
    /// <param name="domain">Either <c>approvals</c> or <c>pr</c>.</param>
    /// <param name="path">Path to the policy file. Defaults to <c>.polyphony-config/policy.yaml</c>;
    /// the <c>POLYPHONY_POLICY_PATH</c> environment variable overrides the default when no
    /// explicit non-default path is supplied.</param>
    [Command("resolve")]
    [VerbResult(typeof(ResolvedRule))]
    public int Resolve(string scope = "", string domain = "", string path = PolicyLoader.DefaultPath)
    {
        if (RequiredInput.HaltIfMissing("policy resolve",
            ("--scope", string.IsNullOrEmpty(scope)),
            ("--domain", string.IsNullOrEmpty(domain))) is { } halt)
            return halt;

        if (!TryParseDomain(domain, out var domainEnum))
        {
            Console.WriteLine($$"""{"error":"Unknown domain '{{domain}}'. Expected 'approvals', 'pr', or 'open_questions'."}""");
            return ExitCodes.ConfigError;
        }

        PolicyConfig config;
        try
        {
            config = PolicyLoader.LoadOrDefaultResolved(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($$"""{"error":"{{EscapeJsonString(ex.Message)}}"}""");
            return ExitCodes.ConfigError;
        }

        ResolvedRule resolved;
        try
        {
            resolved = PolicyResolver.Resolve(config, domainEnum, scope);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($$"""{"error":"{{EscapeJsonString(ex.Message)}}"}""");
            return ExitCodes.ConfigError;
        }

        Console.WriteLine(JsonSerializer.Serialize(resolved, PolyphonyJsonContext.Default.ResolvedRule));
        return ExitCodes.Success;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static PolicyDomainSnapshot SnapshotDomain(DomainPolicy domain)
    {
        var defaults = domain.Defaults!;
        return new PolicyDomainSnapshot
        {
            DefaultsMode = (defaults.Mode ?? PolicyMode.Warning).ToString().ToLowerInvariant(),
            DefaultsMaxRevisionCycles = defaults.MaxRevisionCycles,
            DefaultsMaxFixLoops = defaults.MaxFixLoops,
            DefaultsMaxRemediationCycles = defaults.MaxRemediationCycles,
            DefaultsMinSeverity = defaults.MinSeverity?.ToString().ToLowerInvariant(),
            DefaultsMaxQuestionLoops = defaults.MaxQuestionLoops,
            DefaultsQualityAvgScoreAtLeast = defaults.QualityThreshold?.AvgScoreAtLeast,
            DefaultsQualityBlockingCountAtMost = defaults.QualityThreshold?.BlockingCountAtMost,
            RootMode = domain.Root?.Mode?.ToString().ToLowerInvariant(),
            ByTypeMode = domain.ByType?.ToDictionary(
                kv => kv.Key,
                kv => (kv.Value.Mode ?? defaults.Mode ?? PolicyMode.Warning).ToString().ToLowerInvariant()),
        };
    }

    private static PolicyGuidanceSnapshot SnapshotGuidance(GuidancePolicy guidance)
    {
        var effectiveSource = guidance.Source ?? GuidanceSource.DescriptionBlock;
        return new PolicyGuidanceSnapshot
        {
            Source = effectiveSource,
            AdoFieldName = guidance.AdoFieldName,
            ByTypeSource = guidance.ByType?.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Source ?? effectiveSource),
        };
    }

    private static PolicyRootFallbackSnapshot SnapshotRootFallback(RootFallbackPolicy rootFallback) =>
        new()
        {
            AutoDecide = rootFallback.AutoDecide ?? RootFallbackAutoDecide.Prompt,
        };

    private static PolicyRenegotiationSnapshot SnapshotRenegotiation(RenegotiationPolicy renegotiation) =>
        new()
        {
            AutoDecide = renegotiation.AutoDecide ?? RenegotiationAutoDecide.Prompt,
        };

    private static PolicyUnattendedSnapshot SnapshotUnattended(UnattendedPolicy unattended) =>
        new()
        {
            AcceptanceMode = unattended.AcceptanceMode ?? UnattendedAcceptanceMode.Manual,
            ReviewWaitMode = unattended.ReviewWaitMode ?? UnattendedReviewWaitMode.Wait,
            CapMode = unattended.CapMode ?? UnattendedCapMode.Manual,
        };

    private static (List<string> Errors, List<string> Warnings) ValidateRules(PolicyConfig config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // schema_version must be 1 (already enforced by Parse, but double-check).
        if (config.SchemaVersion != 1)
            errors.Add($"Unsupported schema_version: {config.SchemaVersion}. Expected 1.");

        // Approvals warnings: missing defaults.mode invites surprises.
        if (config.Approvals?.Defaults?.Mode is null)
            warnings.Add("approvals.defaults.mode is not set; will fall back to 'warning' built-in default.");

        if (config.Pr?.Defaults?.Mode is null)
            warnings.Add("pr.defaults.mode is not set; will fall back to 'warning' built-in default.");

        // Caps must be positive.
        WarnIfNonPositive(config.Approvals?.Defaults?.MaxRevisionCycles, "approvals.defaults.max_revision_cycles", errors);
        WarnIfNonPositive(config.Pr?.Defaults?.MaxFixLoops, "pr.defaults.max_fix_loops", errors);
        WarnIfNonPositive(config.Pr?.Defaults?.MaxRemediationCycles, "pr.defaults.max_remediation_cycles", errors);
        WarnIfNonPositive(config.OpenQuestions?.Defaults?.MaxQuestionLoops, "open_questions.defaults.max_question_loops", errors);
        WarnIfNonPositive(config.Concurrency?.MaxConcurrentChildren, "concurrency.max_concurrent_children", errors);

        // Guidance: source must be one of the canonical strings (when set);
        // ado_field requires a non-empty ado_field_name.
        ValidateGuidanceForReporting(config.Guidance, errors);

        // Root fallback: auto_decide must be one of the canonical strings (when set).
        ValidateRootFallbackForReporting(config.RootFallback, errors);

        // Renegotiation: auto_decide must be one of the canonical strings (when set).
        ValidateRenegotiationForReporting(config.Renegotiation, errors);

        return (errors, warnings);
    }

    private static void ValidateRootFallbackForReporting(RootFallbackPolicy? rootFallback, List<string> errors)
    {
        if (rootFallback?.AutoDecide is null) return;
        var value = rootFallback.AutoDecide;
        if (!RootFallbackAutoDecide.IsValid(value))
            errors.Add(
                $"root_fallback.auto_decide '{value}' is not a known auto-decide policy. " +
                $"Expected '{RootFallbackAutoDecide.Prompt}', '{RootFallbackAutoDecide.UseActiveItem}', " +
                $"or '{RootFallbackAutoDecide.Abort}'.");
    }

    private static void ValidateRenegotiationForReporting(RenegotiationPolicy? renegotiation, List<string> errors)
    {
        if (renegotiation?.AutoDecide is null) return;
        var value = renegotiation.AutoDecide;
        if (!RenegotiationAutoDecide.IsValid(value))
            errors.Add(
                $"renegotiation.auto_decide '{value}' is not a known auto-decide policy. " +
                $"Expected '{RenegotiationAutoDecide.Prompt}', '{RenegotiationAutoDecide.AutoRestart}', " +
                $"or '{RenegotiationAutoDecide.Ignore}'.");
    }

    private static void ValidateGuidanceForReporting(GuidancePolicy? guidance, List<string> errors)
    {
        if (guidance is null) return;
        ValidateGuidanceRule("guidance", guidance.Source, guidance.AdoFieldName, errors);

        if (guidance.ByType is null) return;
        foreach (var (typeName, rule) in guidance.ByType)
        {
            var effectiveSource = rule.Source ?? guidance.Source;
            var effectiveField = rule.AdoFieldName ?? guidance.AdoFieldName;
            ValidateGuidanceRule($"guidance.by_type.{typeName}", effectiveSource, effectiveField, errors);
        }
    }

    private static void ValidateGuidanceRule(string scope, string? source, string? adoFieldName, List<string> errors)
    {
        if (source is not null && !GuidanceSource.IsValid(source))
            errors.Add(
                $"{scope}.source '{source}' is not a known guidance source. " +
                $"Expected '{GuidanceSource.DescriptionBlock}' or '{GuidanceSource.AdoField}'.");

        if (source == GuidanceSource.AdoField && string.IsNullOrWhiteSpace(adoFieldName))
            errors.Add(
                $"{scope}.source is '{GuidanceSource.AdoField}' but {scope}.ado_field_name is not set. " +
                "Set ado_field_name to the ADO custom field reference name (e.g. 'Custom.PolyphonyGuidance').");
    }

    private static void WarnIfNonPositive(int? value, string fieldPath, List<string> errors)
    {
        if (value is { } v && v <= 0)
            errors.Add($"{fieldPath} must be positive (got {v}).");
    }

    private static void EmitLoadError(string path, string message)
    {
        // Hand-rolled JSON keeps this AOT-friendly without registering yet another
        // anonymous type with PolyphonyJsonContext.
        Console.WriteLine($$"""{"error":"{{EscapeJsonString(message)}}","source_path":"{{EscapeJsonString(path)}}"}""");
    }

    private static string EscapeJsonString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private static bool TryParseDomain(string value, out PolicyDomain result)
    {
        // Handle underscore-separated form (open_questions) used in CLI and YAML.
        if (string.Equals(value, "open_questions", StringComparison.OrdinalIgnoreCase))
        {
            result = PolicyDomain.OpenQuestions;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out result);
    }
}
