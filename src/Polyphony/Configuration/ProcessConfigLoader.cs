using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Configuration;

public static class ProcessConfigLoader
{
    // Legacy keys retired by the G2 PG removal + MergeGroup consolidation
    // (Polyphony 2.4.0). The loader fails loudly rather than silently
    // ignoring stale keys so operators discover the rename immediately
    // rather than wondering why review policy or branch templates are no
    // longer applied. Each pattern targets a YAML key at any indent, with
    // optional surrounding whitespace and a colon.
    private static readonly (Regex Pattern, string Replacement)[] RetiredKeys = new[]
    {
        (new Regex(@"^\s*pg_branch\s*:", RegexOptions.Multiline), "branch_strategy.merge_group_branch"),
        (new Regex(@"^\s*mg_branch\s*:", RegexOptions.Multiline), "branch_strategy.merge_group_branch"),
        (new Regex(@"^\s*pg_pr\s*:", RegexOptions.Multiline), "review_policies.<section>.merge_group_pr"),
        (new Regex(@"^\s*mg_pr\s*:", RegexOptions.Multiline), "review_policies.<section>.merge_group_pr"),
    };

    public static ProcessConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Process config not found: {path}");

        var yaml = File.ReadAllText(path);

        RejectRetiredKeys(yaml, path);

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

        if (config.SchemaVersion > 1)
            throw new InvalidOperationException(
                $"Unsupported process config schema version {config.SchemaVersion}. " +
                "This version of Polyphony supports schema_version 0 (absent) and 1.");

        return config;
    }

    private static void RejectRetiredKeys(string yaml, string path)
    {
        foreach (var (pattern, replacement) in RetiredKeys)
        {
            var match = pattern.Match(yaml);
            if (!match.Success) continue;

            var lineNumber = yaml.Take(match.Index).Count(static c => c == '\n') + 1;
            var keyName = match.Value.Trim().TrimEnd(':').Trim();
            throw new InvalidOperationException(
                $"Process config '{path}' line {lineNumber}: key '{keyName}' is no longer supported. " +
                $"Rename to '{replacement}'. " +
                "(Polyphony 2.4.0 retired the PG → MergeGroup deprecation aliases; " +
                "see docs/glossary.md and the G2 changelog entry.)");
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

