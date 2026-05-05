using System;
using System.Collections.Generic;

namespace Polyphony.Configuration;

public static class ProcessConfigValidator
{
    /// <summary>
    /// Validates parent-exists (V-15) and cycle-detection (V-16) rules for type parent relationships.
    /// Returns a list of validation errors (empty if valid).
    /// </summary>
    public static List<string> ValidateParentRules(ProcessConfig config)
    {
        var errors = new List<string>();
        var types = config.Types;
        // V-15: parent-exists (case-insensitive lookup, but do not create new dict)
        foreach (var (typeName, typeConfig) in types)
        {
            if (!string.IsNullOrWhiteSpace(typeConfig.Parent))
            {
                var parentExists = types.Keys.Any(k => string.Equals(k, typeConfig.Parent, StringComparison.OrdinalIgnoreCase));
                if (!parentExists)
                {
                    errors.Add($"V-15: Type '{typeName}' declares parent '{typeConfig.Parent}', which does not exist in config.");
                }
            }
        }
        // V-16: cycle-detection
        foreach (var typeName in types.Keys)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = typeName;
            while (true)
            {
                var parent = types[current].Parent;
                if (string.IsNullOrWhiteSpace(parent))
                    break;
                if (!types.ContainsKey(parent))
                    break; // Already reported by V-15
                if (!visited.Add(parent!))
                {
                    errors.Add($"V-16: Cycle detected in parent chain starting at '{typeName}': '{parent}' is repeated.");
                    break;
                }
                current = parent!;
            }
        }
        return errors;
    }
}
