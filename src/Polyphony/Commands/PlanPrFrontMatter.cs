using System.Text.RegularExpressions;
using Polyphony.Manifest;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polyphony.Commands;

/// <summary>
/// Strict-parse outcome for plan-PR front-matter. Distinguishes Absent
/// (no fence) from Malformed (bad YAML) from Present (well-formed),
/// where the lenient <see cref="PlanPrFrontMatter.Parse"/> collapses
/// all three into "safe defaults".
///
/// <para>Used by the P8b diff-validation guard: when a child plan PR
/// touches the parent plan file, the merge-time guard refuses unless
/// the front-matter is Present AND <see cref="RequestsParentChange"/>
/// is true. Malformed/Absent front-matter on a parent-touching PR is
/// itself a blocking outcome.</para>
/// </summary>
/// <param name="Status">Whether the front-matter parsed cleanly.</param>
/// <param name="RequestsParentChange">Value of the <c>requests_parent_change</c> flag (false when not Present).</param>
/// <param name="AncestorPlanGenerations">Snapshot map (empty when not Present).</param>
/// <param name="ErrorDetail">Reason for malformed status; null otherwise.</param>
public sealed record PlanPrFrontMatterStrictResult(
    FrontMatterStatus Status,
    bool RequestsParentChange,
    IReadOnlyDictionary<string, int> AncestorPlanGenerations,
    string? ErrorDetail);

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

    /// <summary>
    /// Strict variant of <see cref="Parse"/> used by the
    /// <c>polyphony pr validate-plan-diff</c> verb. Distinguishes three
    /// outcomes — Absent, Malformed, Present — so the caller can decide
    /// whether to block a parent-plan touch on the absence (or
    /// well-formedness) of the front-matter, rather than silently treating
    /// a malformed body as "no flag set".
    ///
    /// <list type="bullet">
    ///   <item><b>Absent</b> — body is empty, contains no <c>---</c>-fenced
    ///     block at the very start, or the fenced block contains only
    ///     whitespace. Returns safe defaults with
    ///     <see cref="PlanPrFrontMatterStrictResult.ErrorDetail"/> = <c>null</c>.</item>
    ///   <item><b>Malformed</b> — fence found and contains content, but
    ///     YamlDotNet failed to parse it; OR a recognized key carried the
    ///     wrong YAML kind (e.g. <c>requests_parent_change: "yes"</c> as
    ///     a string instead of a YAML bool, or
    ///     <c>ancestor_plan_generations</c> as a sequence/scalar instead of
    ///     a mapping, or any value in that mapping is non-integer).
    ///     <see cref="PlanPrFrontMatterStrictResult.ErrorDetail"/> carries a
    ///     short human-readable reason.</item>
    ///   <item><b>Present</b> — well-formed YAML with all recognized keys
    ///     of the correct shape. Missing recognized keys are allowed and
    ///     default to <c>false</c> / empty map (still Present — sparse but
    ///     correct).</item>
    /// </list>
    ///
    /// <para>The lenient <see cref="Parse"/> entry-point is unchanged so
    /// the polling verb keeps its forgiving behaviour.</para>
    /// </summary>
    public static PlanPrFrontMatterStrictResult ParseStrict(string body)
    {
        var emptyDict = new Dictionary<string, int>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(body))
        {
            return new PlanPrFrontMatterStrictResult(FrontMatterStatus.Absent, false, emptyDict, null);
        }

        var match = FenceRegex.Match(body);
        if (!match.Success)
        {
            return new PlanPrFrontMatterStrictResult(FrontMatterStatus.Absent, false, emptyDict, null);
        }

        var yamlText = match.Groups["yaml"].Value;
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return new PlanPrFrontMatterStrictResult(FrontMatterStatus.Absent, false, emptyDict, null);
        }

        YamlStream yaml;
        try
        {
            yaml = new YamlStream();
            using var reader = new StringReader(yamlText);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            return new PlanPrFrontMatterStrictResult(
                FrontMatterStatus.Malformed,
                false,
                emptyDict,
                $"YAML parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new PlanPrFrontMatterStrictResult(
                FrontMatterStatus.Malformed,
                false,
                emptyDict,
                $"YAML parse error: {ex.Message}");
        }

        if (yaml.Documents.Count == 0)
        {
            return new PlanPrFrontMatterStrictResult(FrontMatterStatus.Absent, false, emptyDict, null);
        }

        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return new PlanPrFrontMatterStrictResult(
                FrontMatterStatus.Malformed,
                false,
                emptyDict,
                "Front-matter root must be a YAML mapping.");
        }

        bool requestsParent = false;
        if (root.Children.TryGetValue(new YamlScalarNode("requests_parent_change"), out var rpcNode))
        {
            if (rpcNode is not YamlScalarNode rpcScalar)
            {
                return new PlanPrFrontMatterStrictResult(
                    FrontMatterStatus.Malformed,
                    false,
                    emptyDict,
                    "'requests_parent_change' must be a YAML boolean (true|false).");
            }

            // Require a real YAML bool — reject quoted strings like "yes",
            // numbers, etc. YamlDotNet tags real bools as
            // "tag:yaml.org,2002:bool"; an untagged plain (unquoted)
            // "true"/"false" scalar is also accepted as a YAML 1.1 bool.
            var tag = rpcScalar.Tag.IsEmpty ? string.Empty : rpcScalar.Tag.Value ?? string.Empty;
            var isPlainBool =
                rpcScalar.Style == ScalarStyle.Plain
                && (string.Equals(rpcScalar.Value, "true", StringComparison.Ordinal)
                    || string.Equals(rpcScalar.Value, "false", StringComparison.Ordinal));
            var isTaggedBool = tag.EndsWith(":bool", StringComparison.Ordinal);

            if (!isPlainBool && !isTaggedBool)
            {
                return new PlanPrFrontMatterStrictResult(
                    FrontMatterStatus.Malformed,
                    false,
                    emptyDict,
                    $"'requests_parent_change' must be an unquoted YAML boolean (true|false); got '{rpcScalar.Value}'.");
            }

            requestsParent = string.Equals(rpcScalar.Value, "true", StringComparison.Ordinal);
        }

        IReadOnlyDictionary<string, int> generations = emptyDict;
        if (root.Children.TryGetValue(new YamlScalarNode("ancestor_plan_generations"), out var apgNode))
        {
            if (apgNode is not YamlMappingNode apgMap)
            {
                return new PlanPrFrontMatterStrictResult(
                    FrontMatterStatus.Malformed,
                    false,
                    emptyDict,
                    "'ancestor_plan_generations' must be a YAML mapping of <ancestor-id> -> <generation:int>.");
            }

            var dict = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kvp in apgMap.Children)
            {
                if (kvp.Key is not YamlScalarNode keyScalar || string.IsNullOrEmpty(keyScalar.Value))
                {
                    return new PlanPrFrontMatterStrictResult(
                        FrontMatterStatus.Malformed,
                        false,
                        emptyDict,
                        "'ancestor_plan_generations' keys must be non-empty scalars.");
                }
                if (kvp.Value is not YamlScalarNode valueScalar || !int.TryParse(valueScalar.Value, out var v))
                {
                    return new PlanPrFrontMatterStrictResult(
                        FrontMatterStatus.Malformed,
                        false,
                        emptyDict,
                        $"'ancestor_plan_generations[{keyScalar.Value}]' must be an integer.");
                }
                dict[keyScalar.Value!] = v;
            }
            generations = dict;
        }

        return new PlanPrFrontMatterStrictResult(FrontMatterStatus.Present, requestsParent, generations, null);
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
