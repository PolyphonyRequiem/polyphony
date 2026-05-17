using System.Text.RegularExpressions;

namespace Polyphony.Commands;

/// <summary>
/// Parser for the <c>&lt;!-- polyphony:agent-comment ... --&gt;</c>
/// HTML marker that polyphony's automated PR-comment posters inject
/// into the first line of every machine-authored comment. The
/// <c>pr_feedback_analyzer</c> agent reads the parsed marker to
/// distinguish bot feedback from human feedback even when both posted
/// via the same operator token (the common ADO case where
/// <c>plan_reviewer_poster_ado</c> shares the operator's PAT).
///
/// <para>Canonical form:</para>
/// <code>
/// &lt;!-- polyphony:agent-comment agent=plan_reviewer head_sha=abc1234 run_id=xyz --&gt;
/// </code>
///
/// <para>The <c>agent</c> attribute is required; <c>head_sha</c> and
/// <c>run_id</c> are optional. Attribute order is not significant.
/// The marker must be on the first non-whitespace line of the
/// comment body; downstream content is unrestricted.</para>
/// </summary>
internal static class PrCommentMarker
{
    /// <summary>
    /// Recognize the marker shape. Anchored to start of body (after
    /// optional leading whitespace). Attribute order is not significant.
    /// Attributes use simple <c>name=value</c> form with no quoting; the
    /// value runs to the next whitespace or the closing <c>--&gt;</c>.
    /// </summary>
    private static readonly Regex MarkerRegex = new(
        @"^\s*<!--\s*polyphony:agent-comment\s+(?<attrs>.+?)\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AttrRegex = new(
        @"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[^\s>]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Attempt to parse the marker out of <paramref name="commentBody"/>.
    /// Returns null when no marker is present, when the marker is
    /// malformed (missing required <c>agent</c> attribute), or when
    /// <paramref name="commentBody"/> is null/empty.
    /// </summary>
    public static PrPollCommentMarker? TryParse(string? commentBody)
    {
        if (string.IsNullOrEmpty(commentBody)) return null;

        var match = MarkerRegex.Match(commentBody);
        if (!match.Success) return null;

        string? agent = null;
        string? headSha = null;
        string? runId = null;

        foreach (Match attr in AttrRegex.Matches(match.Groups["attrs"].Value))
        {
            var key = attr.Groups["key"].Value.ToLowerInvariant();
            var value = attr.Groups["value"].Value;
            switch (key)
            {
                case "agent": agent = value; break;
                case "head_sha": headSha = value; break;
                case "run_id": runId = value; break;
            }
        }

        if (string.IsNullOrEmpty(agent)) return null;

        return new PrPollCommentMarker
        {
            Agent = agent,
            HeadSha = string.IsNullOrEmpty(headSha) ? null : headSha,
            RunId = string.IsNullOrEmpty(runId) ? null : runId,
        };
    }

    /// <summary>
    /// Build a canonical marker string for an automated poster to
    /// prepend to its comment body. Use this helper rather than
    /// hand-formatting so analyzer parsing stays in lockstep with
    /// poster generation. The marker is emitted as a single HTML
    /// comment with the <c>agent</c> attribute first, followed by
    /// optional <c>head_sha</c> and <c>run_id</c> when supplied.
    /// </summary>
    /// <param name="agent">The conductor agent name, e.g. <c>plan_reviewer</c>. Must be non-empty.</param>
    /// <param name="headSha">Optional head SHA at the time of posting.</param>
    /// <param name="runId">Optional conductor run identifier.</param>
    public static string Format(string agent, string? headSha = null, string? runId = null)
    {
        if (string.IsNullOrEmpty(agent))
            throw new ArgumentException("agent is required", nameof(agent));
        var attrs = $"agent={agent}";
        if (!string.IsNullOrEmpty(headSha)) attrs += $" head_sha={headSha}";
        if (!string.IsNullOrEmpty(runId)) attrs += $" run_id={runId}";
        return $"<!-- polyphony:agent-comment {attrs} -->";
    }
}
