using Polyphony.Sdlc;
using Twig.Domain.Enums;

namespace Polyphony.Configuration;

/// <summary>
/// Validates a <see cref="ProcessConfig"/> against the rules family
/// (V-1 through V-21, with V-17 and V-18 retired alongside the G2 PG removal).
/// Rules V-1–V-8, V-15, V-16, V-19, V-20, V-21 produce errors (block execution).
/// Rules V-9–V-14 produce warnings (informational, file-existence checks).
/// </summary>
public static class ConfigValidator
{
    private static readonly HashSet<string> ValidFacets =
        new(StringComparer.OrdinalIgnoreCase) { "plannable", "actionable", "implementable" };

    private static readonly string[] ValidCategoryNames =
        ["proposed", "in_progress", "resolved", "completed", "removed"];

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

        // V-21: declared state→category mapping. Every type with at least one
        // transition must have a `states:` block; every state appearing on the
        // RHS of `transitions:` must be declared in `states:` with a valid
        // category; declared categories must be one of the canonical 5 names.
        // Replaces the deprecated runtime path through StateCategoryResolver
        // heuristics — see issue #281 / docs/decisions/states-in-process-config.md.
        foreach (var typeName in config.Types.Keys)
        {
            var hasTransitions = config.Transitions.TryGetValue(typeName, out var transitions)
                && transitions.Count > 0;
            var typeStates = config.States.TryGetValue(typeName, out var declaredStates)
                ? declaredStates
                : null;

            if (hasTransitions && (typeStates is null || typeStates.Count == 0))
            {
                errors.Add(Error("V-21",
                    $"Type '{typeName}' has transitions but no `states:` block declaring " +
                    "the (state → category) mapping. Declare every state appearing on the " +
                    "right-hand side of `transitions:` under `states:` with one of: " +
                    "proposed, in_progress, resolved, completed, removed."));
                continue;
            }

            if (typeStates is null) continue;

            // Validate that every declared category string parses
            foreach (var (stateName, categoryString) in typeStates)
            {
                if (string.IsNullOrWhiteSpace(categoryString))
                {
                    errors.Add(Error("V-21",
                        $"Type '{typeName}' state '{stateName}' has an empty category. " +
                        $"Valid categories: {string.Join(", ", ValidCategoryNames)}."));
                    continue;
                }

                if (ProcessConfig.ParseCategory(categoryString) == StateCategory.Unknown)
                {
                    errors.Add(Error("V-21",
                        $"Type '{typeName}' state '{stateName}' has invalid category " +
                        $"'{categoryString}'. Valid categories: " +
                        $"{string.Join(", ", ValidCategoryNames)}."));
                }
            }

            // Validate that every transition target is declared in states:
            if (hasTransitions)
            {
                var declaredNames = new HashSet<string>(typeStates.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var (eventName, targetState) in transitions!)
                {
                    if (!declaredNames.Contains(targetState))
                    {
                        errors.Add(Error("V-21",
                            $"Type '{typeName}' transition '{eventName}: {targetState}' " +
                            $"references state '{targetState}' that is not declared in " +
                            "`states:`. Add it under `states:` with one of: " +
                            $"{string.Join(", ", ValidCategoryNames)}."));
                    }
                }
            }
        }

        // V-9 through V-14: file-existence warnings (only when repoRoot is provided)
        if (repoRoot is not null)
        {
            foreach (var typeName in config.Types.Keys)
            {
                var slug = ToSlug(typeName);

                // V-9: type definition file
                var typeDefPath = Path.Combine(repoRoot, ".polyphony-config", "work-item-types", $"{slug}.md");
                if (!File.Exists(typeDefPath))
                {
                    warnings.Add(Warning("V-9",
                        $"Type definition file missing: .polyphony-config/work-item-types/{slug}.md"));
                }

                // V-10: template file
                var templatePath = Path.Combine(repoRoot, ".polyphony-config", "work-item-types", "templates",
                    $"{slug}-template.md");
                if (!File.Exists(templatePath))
                {
                    warnings.Add(Warning("V-10",
                        $"Template file missing: .polyphony-config/work-item-types/templates/{slug}-template.md"));
                }
            }

            // V-11: agent-guidance/<role>.md for each canonical role
            // (architect, coder, reviewer). Per-type refinements at
            // agent-guidance/<role>/<typeslug>.md are optional and not warned.
            foreach (var role in new[] { "architect", "coder", "reviewer" })
            {
                var rolePath = Path.Combine(repoRoot, ".polyphony-config", "agent-guidance", $"{role}.md");
                if (!File.Exists(rolePath))
                {
                    warnings.Add(Warning("V-11",
                        $"Agent guidance file missing: .polyphony-config/agent-guidance/{role}.md"));
                }
            }

            // V-14: profile.yaml
            if (!File.Exists(Path.Combine(repoRoot, ".polyphony-config", "profile.yaml")))
            {
                warnings.Add(Warning("V-14",
                    "Profile file missing: .polyphony-config/profile.yaml"));
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

    private static ConfigValidationDiagnostic Error(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Error };

    private static ConfigValidationDiagnostic Warning(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Warning };
}


