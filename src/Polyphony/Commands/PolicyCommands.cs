using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Policy;

namespace Polyphony.Commands;

/// <summary>
/// Policy verbs (<c>polyphony policy ...</c>). Phase 7 of Epic 2978 — load and
/// resolve <c>.conductor/policy.yaml</c> at the start of a workflow run, then
/// per-step query the effective mode + caps for each scope.
///
/// <list type="bullet">
///   <item><c>load</c> — reads policy.yaml (or defaults) and emits a snapshot for downstream consumption.</item>
///   <item><c>validate</c> — schema-validates a policy file without applying defaults; emits errors/warnings.</item>
///   <item><c>resolve</c> — returns the effective mode + caps for a given scope and domain (most-specific wins).</item>
/// </list>
/// </summary>
public sealed class PolicyCommands
{
    /// <summary>
    /// Loads <c>.conductor/policy.yaml</c> (or <paramref name="path"/> if specified)
    /// and emits a snapshot of the resolved configuration with built-in defaults
    /// applied. When the file does not exist, returns a defaults-only snapshot
    /// with <c>used_defaults: true</c>.
    /// </summary>
    /// <param name="path">Path to the policy file. Defaults to <c>.conductor/policy.yaml</c>.</param>
    [Command("load")]
    public int Load(string path = ".conductor/policy.yaml")
    {
        PolicyConfig config;
        try
        {
            config = PolicyLoader.LoadOrDefault(path);
        }
        catch (Exception ex)
        {
            EmitLoadError(path, ex.Message);
            return ExitCodes.ConfigError;
        }

        var result = new PolicyLoadResult
        {
            SchemaVersion = config.SchemaVersion,
            SourcePath = File.Exists(path) ? path : null,
            UsedDefaults = !File.Exists(path),
            Approvals = SnapshotDomain(config.Approvals!),
            Pr = SnapshotDomain(config.Pr!),
            Concurrency = new PolicyConcurrencySnapshot
            {
                MaxConcurrentChildren = config.Concurrency!.MaxConcurrentChildren!.Value,
                MaxConcurrentPgs = config.Concurrency.MaxConcurrentPgs!.Value,
            },
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PolicyLoadResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Schema-validates a policy file WITHOUT applying built-in defaults. Surfaces
    /// missing required fields and unknown enum values as warnings/errors so the
    /// operator can choose to opt into defaults explicitly.
    /// </summary>
    /// <param name="path">Path to the policy file. Defaults to <c>.conductor/policy.yaml</c>.</param>
    [Command("validate")]
    public int Validate(string path = ".conductor/policy.yaml")
    {
        if (!File.Exists(path))
        {
            var missing = new PolicyValidateResult
            {
                Valid = false,
                SourcePath = path,
                Errors = [$"Policy file not found: {path}"],
                Warnings = [],
            };
            Console.WriteLine(JsonSerializer.Serialize(missing, PolyphonyJsonContext.Default.PolicyValidateResult));
            return ExitCodes.ConfigError;
        }

        PolicyConfig config;
        try
        {
            config = PolicyLoader.Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            var failed = new PolicyValidateResult
            {
                Valid = false,
                SourcePath = path,
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
            SourcePath = path,
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
    /// <param name="path">Path to the policy file. Defaults to <c>.conductor/policy.yaml</c>.</param>
    [Command("resolve")]
    public int Resolve(string scope, string domain, string path = ".conductor/policy.yaml")
    {
        if (!Enum.TryParse<PolicyDomain>(domain, ignoreCase: true, out var domainEnum))
        {
            Console.WriteLine($$"""{"error":"Unknown domain '{{domain}}'. Expected 'approvals' or 'pr'."}""");
            return ExitCodes.ConfigError;
        }

        PolicyConfig config;
        try
        {
            config = PolicyLoader.LoadOrDefault(path);
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
            DefaultsQualityAvgScoreAtLeast = defaults.QualityThreshold?.AvgScoreAtLeast,
            DefaultsQualityBlockingCountAtMost = defaults.QualityThreshold?.BlockingCountAtMost,
            RootMode = domain.Root?.Mode?.ToString().ToLowerInvariant(),
            ByTypeMode = domain.ByType?.ToDictionary(
                kv => kv.Key,
                kv => (kv.Value.Mode ?? defaults.Mode ?? PolicyMode.Warning).ToString().ToLowerInvariant()),
        };
    }

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
        WarnIfNonPositive(config.Concurrency?.MaxConcurrentChildren, "concurrency.max_concurrent_children", errors);
        WarnIfNonPositive(config.Concurrency?.MaxConcurrentPgs, "concurrency.max_concurrent_pgs", errors);

        return (errors, warnings);
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
}
