using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Configuration;

public static class ProcessConfigLoader
{
    public static ProcessConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Process config not found: {path}");

        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<ProcessConfig>(yaml)
            ?? throw new InvalidOperationException("Failed to parse process config");

        // Back-compat: copy the legacy `capabilities:` YAML key onto `Facets`
        // when the new `facets:` key is absent. The capability→facet rename
        // ships in Phase 1 of the PR-lifecycle overhaul; existing process
        // configs continue to work during the migration window.
        // See docs/glossary.md.
        foreach (var typeConfig in config.Types.Values)
        {
            if (typeConfig.Facets.Length == 0 && typeConfig.CapabilitiesLegacy is { Length: > 0 } legacy)
            {
                typeConfig.Facets = legacy;
            }
        }

        // Back-compat: copy the legacy `branch_strategy.pg_branch` YAML key
        // onto `MergeGroupBranch` when the new `mg_branch:` key is absent. The
        // PG→MergeGroup rename ships in Phase 4 of the PR-lifecycle overhaul;
        // existing process configs continue to work during the migration
        // window. The validator emits V-17 when this fallback fires.
        if (config.BranchStrategy is { } branchStrategy
            && string.IsNullOrEmpty(branchStrategy.MergeGroupBranch)
            && !string.IsNullOrEmpty(branchStrategy.PgBranch))
        {
            branchStrategy.MergeGroupBranch = branchStrategy.PgBranch;
        }

        // Back-compat: copy the legacy `pg_pr:` policy key onto `mg_pr` for
        // each ReviewPolicies dictionary (planning, implementation,
        // remediation) when the new `mg_pr:` key is absent. Workflow YAMLs
        // that read `mg_pr` continue to function against legacy configs.
        // The validator emits V-18 when this fallback fires.
        if (config.ReviewPolicies is { } reviewPolicies)
        {
            CopyLegacyPolicyKey(reviewPolicies.Planning);
            CopyLegacyPolicyKey(reviewPolicies.Implementation);
            CopyLegacyPolicyKey(reviewPolicies.Remediation);
        }

        if (config.SchemaVersion > 1)
            throw new InvalidOperationException(
                $"Unsupported process config schema version {config.SchemaVersion}. " +
                "This version of Polyphony supports schema_version 0 (absent) and 1.");

        return config;
    }

    /// <summary>
    /// Copies the legacy <c>pg_pr</c> policy entry onto <c>mg_pr</c> when the
    /// latter is absent. Operates in-place on the supplied dictionary.
    /// </summary>
    private static void CopyLegacyPolicyKey(Dictionary<string, ReviewPolicy>? policies)
    {
        if (policies is null) return;
        if (policies.ContainsKey("mg_pr")) return;
        if (policies.TryGetValue("pg_pr", out var legacy))
        {
            policies["mg_pr"] = legacy;
        }
    }

    /// <summary>
    /// Returns the parent type name for the given type, or null if none is set.
    /// </summary>
    public static string? GetParentTypeName(ProcessConfig config, string typeName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (typeName is null) throw new ArgumentNullException(nameof(typeName));
        if (!config.Types.TryGetValue(typeName, out var typeConfig))
            throw new ArgumentException($"Type '{typeName}' not found in process config.", nameof(typeName));
        return typeConfig.Parent;
    }
}

