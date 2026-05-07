using System.Text.RegularExpressions;

namespace Polyphony.Manifest;

/// <summary>
/// Phase 3 P8: pure extractor for the <c>requests-parent-change</c>
/// renegotiation block embedded in a plan-PR body. Mirrors the
/// HTML-comment-fenced convention introduced for per-item guidance in
/// PR #133 (<see cref="Polyphony.Guidance.GuidanceExtractor"/>) so a
/// child planner can declare "the scope I was given was wrong — please
/// revisit" inline in the PR description.
///
/// <para>Convention:
/// <code>
/// &lt;!-- polyphony:requests-parent-change --&gt;
/// &lt;reason text&gt;
/// &lt;!-- /polyphony:requests-parent-change --&gt;
/// </code>
/// Multiple blocks are concatenated with a single blank line
/// (<c>"\n\n"</c>) so a planner can pile up several requests without
/// losing structure. An opening tag without a matching closing tag is
/// reported as <see cref="ExtractStatus.Malformed"/> — the workflow
/// surfaces it rather than silently dropping intent.</para>
///
/// <para>This module is pure: input is a string body, output is a value
/// record. The verb adapter (<c>polyphony plan extract-renegotiation-flag</c>)
/// owns the I/O (gh fetch, JSON envelope).</para>
/// </summary>
public static class RenegotiationFlagExtractor
{
    /// <summary>HTML opening marker for the renegotiation fence.</summary>
    public const string OpenTag = "<!-- polyphony:requests-parent-change -->";

    /// <summary>HTML closing marker for the renegotiation fence.</summary>
    public const string CloseTag = "<!-- /polyphony:requests-parent-change -->";

    /// <summary>
    /// Separator inserted between concatenated renegotiation blocks when
    /// a PR body carries more than one. Single blank line — distinct from
    /// <see cref="Polyphony.Guidance.GuidanceExtractor.BlockSeparator"/>
    /// (which uses an HR rule) because renegotiation blocks are reasons
    /// being read by a downstream planner agent, not standalone guidance
    /// docs.
    /// </summary>
    public const string BlockSeparator = "\n\n";

    private static readonly Regex BlockRegex = new(
        @"<!--\s*polyphony:requests-parent-change\s*-->(.*?)<!--\s*/polyphony:requests-parent-change\s*-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Detects an opening tag that is NOT followed by a closing tag. We
    /// use this AFTER the well-formed block regex has consumed every
    /// matched pair so the leftover-opens classify as malformed.
    /// </summary>
    private static readonly Regex OpenTagRegex = new(
        @"<!--\s*polyphony:requests-parent-change\s*-->",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Outcome of <see cref="Extract"/>. Distinguishes the three states
    /// downstream consumers need to route on.
    /// </summary>
    public enum ExtractStatus
    {
        /// <summary>No opening tag was present. <see cref="Result.Reason"/> is null.</summary>
        Absent,

        /// <summary>One or more well-formed blocks were extracted. <see cref="Result.Reason"/> is non-null (may be empty if the block was whitespace-only — see field doc).</summary>
        Present,

        /// <summary>An opening tag exists with no matching closing tag. <see cref="Result.Reason"/> is null.</summary>
        Malformed,
    }

    /// <summary>
    /// Result of <see cref="Extract"/>. <see cref="FlagPresent"/> is the
    /// boolean projection consumers can route on without re-checking the
    /// nullable string.
    /// </summary>
    /// <param name="Status">The classification.</param>
    /// <param name="FlagPresent">True iff <see cref="Status"/> is <see cref="ExtractStatus.Present"/>.</param>
    /// <param name="Reason">Concatenated trimmed inner text from every well-formed block, or null when none.</param>
    public sealed record Result(
        ExtractStatus Status,
        bool FlagPresent,
        string? Reason);

    /// <summary>
    /// Extract renegotiation-block content from <paramref name="body"/>.
    /// Null/empty body → <see cref="ExtractStatus.Absent"/>. Multiple
    /// well-formed blocks are concatenated with <see cref="BlockSeparator"/>;
    /// each block's inner content is trimmed of outer whitespace before
    /// joining (inner whitespace preserved). Whitespace-only blocks
    /// contribute an empty string to the join — the flag is still
    /// <see cref="ExtractStatus.Present"/>, but the resulting reason may
    /// be empty after trimming.
    /// </summary>
    public static Result Extract(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new Result(ExtractStatus.Absent, FlagPresent: false, Reason: null);
        }

        var matches = BlockRegex.Matches(body);
        if (matches.Count == 0)
        {
            // No well-formed pair. Look for an unmatched opener — that's
            // a malformed block.
            return OpenTagRegex.IsMatch(body)
                ? new Result(ExtractStatus.Malformed, FlagPresent: false, Reason: null)
                : new Result(ExtractStatus.Absent, FlagPresent: false, Reason: null);
        }

        // Belt-and-braces: even if we matched at least one well-formed
        // block, an extra trailing opener with no closer is still
        // malformed intent we should not silently drop. Count opening
        // tags and compare against the matched-pair count.
        var openCount = OpenTagRegex.Matches(body).Count;
        if (openCount > matches.Count)
        {
            return new Result(ExtractStatus.Malformed, FlagPresent: false, Reason: null);
        }

        var parts = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            parts.Add(match.Groups[1].Value.Trim());
        }
        var reason = string.Join(BlockSeparator, parts);
        return new Result(ExtractStatus.Present, FlagPresent: true, Reason: reason);
    }
}
