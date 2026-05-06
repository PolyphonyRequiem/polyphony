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

    // Tighter variant used only by ReplaceSnapshotPreservingTail. It
    // captures the front-matter block up to and including the CLOSING
    // <c>---</c> only — NOT the line ending that follows it. The original
    // line ending after the closing fence (LF, CRLF, or end-of-string)
    // is therefore part of the body tail, which lets the rewriter splice
    // in a fresh front-matter block while the tail bytes (including their
    // exact line endings) round-trip verbatim. The opening fence may have
    // any leading whitespace on its own line; the closing fence may have
    // trailing spaces/tabs but not a newline.
    private static readonly Regex RewriteFenceRegex = new(
        @"\A---[ \t]*\r?\n(?<yaml>.*?)\r?\n---[ \t]*",
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

    /// <summary>
    /// Replace the <c>ancestor_plan_generations</c> snapshot inside an
    /// existing plan-PR body, preserving the <c>requests_parent_change</c>
    /// flag and the body tail (everything below the closing <c>---</c>
    /// fence) byte-for-byte. Used by the P9 cascade remedy after a clean
    /// auto-rebase to update the PR's snapshot to match the manifest's
    /// current <c>plan_generations</c>.
    ///
    /// <para><b>Outcomes</b> (<see cref="FrontMatterReplacement"/>):</para>
    /// <list type="bullet">
    ///   <item><see cref="FrontMatterReplacement.Replaced"/> — front-matter
    ///     was Present and well-formed; <c>NewBody</c> contains the rewritten
    ///     body. <c>requests_parent_change</c> is carried over verbatim;
    ///     <c>ancestor_plan_generations</c> is replaced wholesale by
    ///     <paramref name="newAncestorPlanGenerations"/> serialised in
    ///     deterministic key-sorted order.</item>
    ///   <item><see cref="FrontMatterReplacement.Malformed"/> — front-matter
    ///     was present but failed strict parsing. The cascade-remedy verb
    ///     refuses to rewrite a malformed body and surfaces
    ///     <c>malformed_front_matter</c> as its outcome.</item>
    ///   <item><see cref="FrontMatterReplacement.Absent"/> — no fenced
    ///     front-matter at the start of the body. The cascade-remedy verb
    ///     also refuses (a plan-PR without front-matter is a hand-edited
    ///     special case the workflow shouldn't silently overwrite).</item>
    /// </list>
    ///
    /// <para><b>Tail preservation:</b> the regex used for detection is
    /// non-greedy on the YAML block, so a body containing additional
    /// <c>---</c>-only lines (markdown horizontal rules) below the closing
    /// fence is safe — only the FIRST closing fence is treated as the
    /// front-matter boundary. The serialised front-matter is emitted with
    /// <c>\n</c> line endings; the tail is appended verbatim including
    /// whatever line endings (CRLF/LF/mixed) it originally had, so a
    /// CRLF-tailed body round-trips exactly in its tail bytes.</para>
    ///
    /// <para>The serialiser uses the canonical block-style YAML the rest
    /// of the codebase emits and key-sorts the snapshot so two callers
    /// passing the same dictionary produce byte-identical output.</para>
    /// </summary>
    /// <param name="body">Existing PR body to rewrite. May be empty.</param>
    /// <param name="newAncestorPlanGenerations">Replacement snapshot. Keys are normalised plan-keys (<c>"root"</c> or numeric ids as strings); values are the manifest's current generations.</param>
    public static FrontMatterReplacement ReplaceSnapshotPreservingTail(
        string body,
        IReadOnlyDictionary<string, int> newAncestorPlanGenerations)
    {
        ArgumentNullException.ThrowIfNull(newAncestorPlanGenerations);
        body ??= string.Empty;

        var strict = ParseStrict(body);
        switch (strict.Status)
        {
            case FrontMatterStatus.Absent:
                return new FrontMatterReplacement.Absent();
            case FrontMatterStatus.Malformed:
                return new FrontMatterReplacement.Malformed(strict.ErrorDetail ?? "Malformed front-matter.");
        }

        // Locate the front-matter span so we can splice in the rewritten
        // block while preserving the tail bytes verbatim. ParseStrict
        // already proved a fence exists. Use the rewrite-specific regex
        // that does NOT consume the line ending after the closing ---,
        // so the original tail (including its line endings) flows through
        // unchanged.
        var match = RewriteFenceRegex.Match(body);
        if (!match.Success)
        {
            // Defensive: ParseStrict says Present but the regex disagrees.
            // Treat as Absent so the caller can refuse rather than emit
            // a body without a fence.
            return new FrontMatterReplacement.Absent();
        }

        var tail = body[match.Length..];
        var rewritten = SerialiseFrontMatter(strict.RequestsParentChange, newAncestorPlanGenerations) + tail;
        return new FrontMatterReplacement.Replaced(rewritten);
    }

    /// <summary>
    /// Emit the canonical front-matter block: opening fence, deterministic
    /// key order (<c>requests_parent_change</c> first, then
    /// <c>ancestor_plan_generations</c> with sorted keys), closing fence.
    /// Output line endings are <c>\n</c>. The closing <c>---</c> is emitted
    /// WITHOUT a trailing newline — the body tail (appended by the caller)
    /// carries the line ending that originally followed the closing fence,
    /// so a CRLF body's tail round-trips byte-exactly and a body whose tail
    /// is empty (front-matter only, no body) ends with exactly one <c>\n</c>
    /// after the closing fence.
    /// </summary>
    private static string SerialiseFrontMatter(
        bool requestsParentChange,
        IReadOnlyDictionary<string, int> generations)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("---\n");
        sb.Append("requests_parent_change: ");
        sb.Append(requestsParentChange ? "true" : "false");
        sb.Append('\n');
        sb.Append("ancestor_plan_generations:\n");
        foreach (var kvp in generations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append("  ");
            sb.Append(SerialiseYamlKey(kvp.Key));
            sb.Append(": ");
            sb.Append(kvp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        sb.Append("---");
        return sb.ToString();
    }

    /// <summary>
    /// Quote a snapshot key only when it would otherwise parse as a
    /// non-string YAML scalar (the all-digits case for numeric work-item
    /// ids — left unquoted YAML 1.2 would still treat them as strings, but
    /// the existing emitters in this codebase quote them, and we preserve
    /// that convention so emitted bodies round-trip identically).
    /// </summary>
    private static string SerialiseYamlKey(string key)
    {
        if (key.Length > 0 && key.All(char.IsDigit))
        {
            return $"\"{key}\"";
        }
        return key;
    }
}

/// <summary>
/// Outcome of <see cref="PlanPrFrontMatter.ReplaceSnapshotPreservingTail"/>.
/// Discriminated union so the cascade-remedy verb can route on the three
/// terminal cases — replaced (write back to the PR), malformed (refuse),
/// absent (refuse) — without nullable-string sniffing.
/// </summary>
public abstract record FrontMatterReplacement
{
    /// <summary>Front-matter was Present; <see cref="NewBody"/> is the rewritten body to write back.</summary>
    public sealed record Replaced(string NewBody) : FrontMatterReplacement;

    /// <summary>Front-matter parsed strictly as Malformed; the verb refuses to rewrite. <see cref="Reason"/> mirrors <see cref="PlanPrFrontMatterStrictResult.ErrorDetail"/>.</summary>
    public sealed record Malformed(string Reason) : FrontMatterReplacement;

    /// <summary>Body had no fenced front-matter at the start; the verb refuses to invent one (a hand-written plan PR is out of scope).</summary>
    public sealed record Absent : FrontMatterReplacement;
}
