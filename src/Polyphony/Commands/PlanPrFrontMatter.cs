using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Polyphony.Commands;

/// <summary>
/// Minimal front-matter parser for plan-PR bodies. Plan PRs (opened by
/// <c>polyphony pr open-plan-pr</c>) embed two well-known keys at the
/// top of the body inside a <c>---</c>-fenced block:
/// <code>
/// ---
/// requests_parent_change: true
/// ancestor_plan_generations:
///   root: 2
///   "5678": 1
/// ---
///
/// ## Plan body...
/// </code>
/// All other body content is ignored. The fence MUST be the very first
/// thing in the body — anything before it is treated as "no front-matter
/// present" and the result returns the safe defaults
/// (<c>requests_parent_change=false</c>, empty map). This conservative
/// behavior keeps the parser from misreading hand-written plan PRs that
/// happen to mention the keys in prose later in the body.
///
/// <para>Returns the same defaults when YAML parsing fails — front-matter
/// is opportunistic context, not authoritative.</para>
/// </summary>
internal static class PlanPrFrontMatter
{
    private static readonly Regex FenceRegex = new(
        @"\A---\s*\r?\n(?<yaml>.*?)\r?\n---\s*(\r?\n|$)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static PrPollMetadata Parse(string body)
    {
        var defaults = new PrPollMetadata
        {
            RequestsParentChange = false,
            AncestorPlanGenerations = new Dictionary<string, int>(StringComparer.Ordinal),
        };

        if (string.IsNullOrEmpty(body)) return defaults;

        var match = FenceRegex.Match(body);
        if (!match.Success) return defaults;

        var yamlText = match.Groups["yaml"].Value;
        if (string.IsNullOrWhiteSpace(yamlText)) return defaults;

        try
        {
            var yaml = new YamlStream();
            using var reader = new StringReader(yamlText);
            yaml.Load(reader);
            if (yaml.Documents.Count == 0) return defaults;
            if (yaml.Documents[0].RootNode is not YamlMappingNode root) return defaults;

            var requestsParent = ReadBool(root, "requests_parent_change");
            var generations = ReadIntMap(root, "ancestor_plan_generations");

            return new PrPollMetadata
            {
                RequestsParentChange = requestsParent,
                AncestorPlanGenerations = generations,
            };
        }
        catch
        {
            // Malformed YAML — fall back to safe defaults rather than failing
            // the verb. The plan PR body is human-editable; bad YAML should
            // not prevent the workflow from polling status.
            return defaults;
        }
    }

    private static bool ReadBool(YamlMappingNode root, string key)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node)) return false;
        if (node is not YamlScalarNode scalar) return false;
        return string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, int> ReadIntMap(YamlMappingNode root, string key)
    {
        var empty = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node)) return empty;
        if (node is not YamlMappingNode mapping) return empty;

        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kvp in mapping.Children)
        {
            if (kvp.Key is not YamlScalarNode keyScalar) continue;
            if (kvp.Value is not YamlScalarNode valueScalar) continue;
            var k = keyScalar.Value;
            if (string.IsNullOrEmpty(k)) continue;
            if (!int.TryParse(valueScalar.Value, out var v)) continue;
            dict[k] = v;
        }
        return dict;
    }
}
