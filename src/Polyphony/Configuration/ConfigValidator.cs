namespace Polyphony.Configuration;

/// <summary>
/// Validates a <see cref="ProcessConfig"/> against 16 rules (V-1 through V-16).
/// Rules V-1–V-8, V-15, V-16 produce errors (block execution).
/// Rules V-9–V-14 produce warnings (informational, file-existence checks).
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

    private static ConfigValidationDiagnostic Error(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Error };

    private static ConfigValidationDiagnostic Warning(string ruleId, string message) =>
        new() { RuleId = ruleId, Message = message, Severity = ConfigValidationSeverity.Warning };
}


