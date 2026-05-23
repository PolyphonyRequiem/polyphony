using System.Text.RegularExpressions;

namespace Polyphony.Commands;

/// <summary>
/// W6 (AB#3280): hidden run-id marker prepended to the *first* line
/// of non-plan PR bodies (impl, MG, evidence, feature). Plan PRs use
/// YAML front-matter for the same purpose (see W7).
///
/// <para>Canonical form:</para>
/// <code>
/// &lt;!-- polyphony:run_id=01HZK7Y9ABCDEF0123456789AB --&gt;
/// </code>
///
/// <para>The marker MUST be prepended (not appended) because ADO
/// truncates PR descriptions at 4000 characters — a trailing marker
/// would silently disappear on long bodies. Reader semantics are
/// shared with <see cref="PrCommentMarker"/>: case-insensitive,
/// anchored to the first non-whitespace line, single-attribute
/// (<c>run_id</c>).</para>
///
/// <para>This is a sibling helper to <see cref="PrCommentMarker"/>;
/// they intentionally use different element names
/// (<c>polyphony:agent-comment</c> for comments,
/// <c>polyphony:run_id</c> for bodies) so a reader can't
/// accidentally cross the streams.</para>
/// </summary>
internal static class PrBodyMarker
{
    /// <summary>
    /// Recognize the run-id marker shape on the first non-whitespace
    /// line of a PR body.
    /// </summary>
    private static readonly Regex MarkerRegex = new(
        @"^\s*<!--\s*polyphony:run_id\s*=\s*(?<run_id>[A-Za-z0-9_-]+)\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Build a canonical marker string. Use this rather than
    /// hand-formatting so reader parsing stays in lockstep.
    /// </summary>
    public static string Format(string runId)
    {
        if (string.IsNullOrEmpty(runId))
            throw new ArgumentException("runId is required", nameof(runId));
        return $"<!-- polyphony:run_id={runId} -->";
    }

    /// <summary>
    /// Ensure <paramref name="body"/> starts with a run-id marker for
    /// <paramref name="runId"/>. Idempotent: returns the body
    /// unchanged when it already starts with any run-id marker (even
    /// one belonging to a different lineage — overwriting an existing
    /// marker would lie about who opened the PR; the caller is
    /// responsible for whatever foreign-PR check is appropriate, see
    /// W9). Returns the body unchanged when <paramref name="runId"/>
    /// is null/empty/whitespace so legacy callers that don't yet
    /// supply a run id continue to work.
    /// </summary>
    public static string EnsureRunIdPrefix(string body, string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return body;
        if (body is null) return Format(runId!);
        if (MarkerRegex.IsMatch(body)) return body;
        return Format(runId!) + Environment.NewLine + body;
    }

    /// <summary>
    /// Extract a run id from the first non-whitespace line of
    /// <paramref name="body"/>. Returns null when no marker is
    /// present, when the body is null/empty, or when the marker is
    /// malformed. Used by readers (state observers, foreign-PR
    /// detection in W9) that need to ground a PR observation in a
    /// lineage when the journal is silent.
    /// </summary>
    public static string? TryParseRunId(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var match = MarkerRegex.Match(body);
        if (!match.Success) return null;
        var runId = match.Groups["run_id"].Value;
        return string.IsNullOrEmpty(runId) ? null : runId;
    }
}
