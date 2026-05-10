using Polyphony.Sdlc;

namespace Polyphony.Configuration;

/// <summary>
/// Validates a <see cref="ProcessConfig"/> against 20 rules (V-1 through V-20).
/// Rules V-1–V-8, V-15, V-16, V-19, V-20 produce errors (block execution).
/// Rules V-9–V-14, V-17, V-18 produce warnings (informational, deprecation,
/// file-existence checks).
/// </summary>
public static class ConfigValidator
{
    private static readonly HashSet<string> ValidFacets =
        new(StringComparer.OrdinalIgnoreCase) { "plannable", "actionable", "implementable" };

    /// <summary>
    /// Validates the given <paramref name="config"/> and returns a structured result.
    /// </summary>
    /// <param name="config">The process config to validate.</param>
    /// <param name="repoRoot">
    /// Optional repository root path for file-existence checks (V-9 through V-14).
    /// When null, file-existence warnings are skipped.
    /// </param>
    public static ConfigValidationResult Validate(ProcessConfig config, string? repoRoot = null)
    {
        var errors = new List<ConfigValidationDiagnostic>();
        var warnings = new List<ConfigValidationDiagnostic>();

        // V-1: process_template required
        if (string.IsNullOrWhiteSpace(config.ProcessTemplate))
        {
            errors.Add(Error("V-1", "process_template is required."));
        }

        // V-2: types list non-empty
        if (config.Types.Count == 0)
        {
            errors.Add(Error("V-2", "At least one type must be defined."));
        }

        var definedTypes = new HashSet<string>(config.Types.Keys, StringComparer.OrdinalIgnoreCase);

        // V-15/V-16: parent-exists and cycle-detection
        var parentErrors = ProcessConfigValidator.ValidateParentRules(config);
        foreach (var err in parentErrors)
        {
            if (err.StartsWith("V-15"))
            {
                errors.Add(Error("V-15", err.Substring(6)));
            }
            else if (err.StartsWith("V-16"))
            {
                errors.Add(Error("V-16", err.Substring(6)));
            }
        }

        // V-7: no duplicate type names (case-insensitive)
        // Dictionary keys are unique by exact match, but we check case-insensitive collisions.
        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var typeName in config.Types.Keys)
        {
            if (!seenTypes.Add(typeName))
            {
                errors.Add(Error("V-7", $"Duplicate type name (case-insensitive): '{typeName}'."));
            }
        }

        foreach (var (typeName, typeConfig) in config.Types)
        {
            // V-3: each type has at least one facet
            if (typeConfig.Facets.Length == 0)
            {
                errors.Add(Error("V-3", $"Type '{typeName}' must have at least one facet."));
            }

            // V-4: facet values are valid (plannable, implementable)
            foreach (var facet in typeConfig.Facets)
            {
                if (!ValidFacets.Contains(facet))
                {
                    errors.Add(Error("V-4",
                        $"Type '{typeName}' has invalid facet '{facet}'. " +
                        "Valid values: plannable, actionable, implementable."));
                }
            }

            // V-5: each type has at least one transition
            if (!config.Transitions.TryGetValue(typeName, out var transitions) || transitions.Count == 0)
            {
                errors.Add(Error("V-5", $"Type '{typeName}' must have at least one transition."));
            }

            // V-8: allowed_child_types reference defined types
            foreach (var childType in typeConfig.AllowedChildTypes)
            {
                if (!definedTypes.Contains(childType))
                {
                    errors.Add(Error("V-8",
                        $"Type '{typeName}' references undefined allowed_child_type '{childType}'."));
                }
            }

            // V-19: execution_mode, when specified, must be a known value.
            // Empty/whitespace is treated as "unset" — the resolver falls
            // back to the documented default (parallel) and no error is
            // raised. Unknown non-empty strings are rejected here so the
            // failure surfaces at config-load time, not at edge-graph build
            // time in PR #5.
            if (!string.IsNullOrWhiteSpace(typeConfig.ExecutionMode)
                && !ExecutionMode.IsValid(typeConfig.ExecutionMode))
            {
                errors.Add(Error("V-19",
                    $"Type '{typeName}' has invalid execution_mode '{typeConfig.ExecutionMode}'. " +
                    $"Valid values: {ExecutionMode.Parallel}, {ExecutionMode.PlanThenImplement}."));
            }
        }

        // V-6: transition keys reference defined types
        foreach (var transitionType in config.Transitions.Keys)
        {
            if (!definedTypes.Contains(transitionType))
            {
                errors.Add(Error("V-6",
                    $"Transitions reference undefined type '{transitionType}'."));
            }
        }

        // V-20: top-level facets block — duplicate skill/MCP names within a
        // single facet's list are almost certainly a typo. Cross-facet
        // identical names are fine (the composer dedupes them silently);
        // see FacetProfileValidator.
        errors.AddRange(FacetProfileValidator.Validate(config));

        // V-17: deprecation warning for legacy `branch_strategy.pg_branch` key.
        // The loader has already copied PgBranch onto MergeGroupBranch for back-compat;
        // we surface the warning here so operators see it via validate-config.
        if (config.BranchStrategy is { } branchStrategy
            && !string.IsNullOrEmpty(branchStrategy.PgBranch))
        {
            warnings.Add(Warning("V-17",
                "branch_strategy.pg_branch is deprecated. Rename to branch_strategy.mg_branch. " +
                "The PG → merge-group rename ships in Phase 4 of the PR-lifecycle overhaul; " +
                "the legacy key continues to work during the migration window."));
        }

        // V-18: deprecation warning for legacy `pg_pr` review-policy key.
        if (config.ReviewPolicies is { } reviewPolicies)
        {
            CheckLegacyPgPrKey(reviewPolicies.Planning, "planning", warnings);
            CheckLegacyPgPrKey(reviewPolicies.Implementation, "implementation", warnings);
            CheckLegacyPgPrKey(reviewPolicies.Remediation, "remediation", warnings);
        }

        // V-9 through V-14: file-existence warnings (only when repoRoot is provided)
        if (repoRoot is not null)
        {
            foreach (var typeName in config.Types.Keys)
            {
                var slug = ToSlug(typeName);

                // V-9: type definition file
                var typeDefPath = Path.Combine(repoRoot, ".conductor", "work-item-types", $"{slug}.md");
                if (!File.Exists(typeDefPath))
                {
                    warnings.Add(Warning("V-9",
                        $"Type definition file missing: .conductor/work-item-types/{slug}.md"));
                }

                // V-10: template file
                var templatePath = Path.Combine(repoRoot, ".conductor", "work-item-types", "templates",
                    $"{slug}-template.md");
                if (!File.Exists(templatePath))
                {
                    warnings.Add(Warning("V-10",
                        $"Template file missing: .conductor/work-item-types/templates/{slug}-template.md"));
                }
            }

            // V-11–V-13: agent-guidance/<type>.md for each type
            foreach (var typeName in config.Types.Keys)
            {
                var slug = ToSlug(typeName);
                var guidancePath = Path.Combine(repoRoot, ".conductor", "agent-guidance", $"{slug}.md");
                if (!File.Exists(guidancePath))
                {
                    warnings.Add(Warning("V-11",
                        $"Agent guidance file missing: .conductor/agent-guidance/{slug}.md"));
                }
            }

            // V-14: profile.yaml
            if (!File.Exists(Path.Combine(repoRoot, ".conductor", "profile.yaml")))
            {
                warnings.Add(Warning("V-14",
                    "Profile file missing: .conductor/profile.yaml"));
            }
        }

        return new ConfigValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors.ToArray(),
            Warnings = warnings.ToArray(),
        };
    }

    /// <summary>
    /// Normalizes a type name to a file-system slug (lowercase, spaces to hyphens).
    /// </summary>
    public static string ToSlug(string typeName) =>
        typeName.ToLowerInvariant().Replace(' ', '-');

    /// <summary>
    /// Adds a V-18 deprecation warning when the supplied policy dictionary
    /// contains the legacy <c>pg_pr</c> key.
    /// </summary>
    private static void CheckLegacyPgPrKey(
        Dictionary<string, ReviewPolicy>? policies,
        string section,
        List<ConfigValidationDiagnostic> warnings)
    {
        if (policies is null) return;
        if (!policies.ContainsKey("pg_pr")) return;

        warnings.Add(Warning("V-18",
            $"review_policies.{section}.pg_pr is deprecated. Rename to mg_pr. " +
            "The PG → merge-group rename ships in Phase 4 of the PR-lifecycle overhaul; " +
            "the legacy key continues to work during the migration window."));
    }

    private static ConfigValidationDiagnostic Error(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Error };

    private static ConfigValidationDiagnostic Warning(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Warning };
}


