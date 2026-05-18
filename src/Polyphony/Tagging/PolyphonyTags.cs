using System.Globalization;

namespace Polyphony.Tagging;

/// <summary>
/// Constants for the <c>polyphony:*</c> tag namespace stamped on ADO work
/// items by the polyphony pipeline.
///
/// Authoritative spec: <c>docs/polyphony-tags.md</c>.
/// </summary>
public static class PolyphonyTags
{
    /// <summary>
    /// Bare scope-ownership tag. Indicates a descendant of a root that is
    /// in-scope for the polyphony pipeline.
    /// </summary>
    public const string InScope = "polyphony";

    /// <summary>
    /// Marks an item as a root for the polyphony pipeline. Implies in-scope.
    /// Used by <c>polyphony root resolve</c> to walk up to the nearest root.
    /// </summary>
    public const string Root = "polyphony:root";

    /// <summary>
    /// Status sub-tag set by the planner agent on plan completion. Pre-existing
    /// behaviour, preserved as-is. Read by the plan-level workflow's
    /// resume-detection gate.
    /// </summary>
    public const string Planned = "polyphony:planned";

    /// <summary>
    /// Tag-name prefix for the per-item facet override (closed-loop PR #7).
    /// Stamped by <c>polyphony plan seed-children</c> as
    /// <c>polyphony:facets=&lt;csv&gt;</c> when the architect declared
    /// <c>apex_facets</c> in plan front-matter for an indivisible apex.
    /// Read by <see cref="Sdlc.RequirementInputResolver"/> to override the
    /// type-config default facet set on a per-call basis. The full tag
    /// shape (prefix + <c>=</c> + canonical csv) is owned by
    /// <see cref="Sdlc.FacetTagParser"/>.
    /// </summary>
    public const string FacetsPrefix = "polyphony:facets";

    /// <summary>
    /// Tag-name prefix for the per-MG "this item's impl PR has already been
    /// merged into this merge group's branch" marker (AB#3217 follow-up to
    /// AB#3169). Stamped as
    /// <c>polyphony:impl-merged-in-mg=&lt;mg-key&gt;</c> by
    /// <c>polyphony branch mark-impl-merged</c> at the end of
    /// <c>primary_completer</c>'s apex-root branch; cleared by
    /// <c>polyphony branch clear-impl-merged</c> on every workflow route
    /// that re-dispatches the same MG for revision
    /// (scope_revise_counter, scope_revise_reset, user_acceptance
    /// Request Changes). Read by <see cref="Commands.BranchCommands.NextImpl"/>
    /// to skip the apex root and report <c>all_items_done</c>, breaking
    /// the redispatch loop documented in AB#3217.
    ///
    /// Without this tag: when the apex root is the sole implementable item
    /// in its merge group, <c>primary_completer</c> deliberately does NOT
    /// transition state (terminal transition is deferred to
    /// <c>close_mark_satisfied</c> per AB#3169 so feature → main has
    /// promoted first); <c>primary_router</c> filters only on terminal
    /// state and re-dispatches the same item forever, each iteration
    /// producing an empty squash-coverage mismatch.
    /// </summary>
    public const string ImplMergedInMgPrefix = "polyphony:impl-merged-in-mg";

    /// <summary>
    /// Returns true if the given parsed tag set indicates the item is in-scope
    /// (carries either <see cref="InScope"/> or <see cref="Root"/>).
    /// </summary>
    public static bool IsInScope(TagSet tags) =>
        tags.Contains(InScope) || tags.Contains(Root);

    /// <summary>
    /// Returns true if the given parsed tag set marks this item as a root.
    /// </summary>
    public static bool IsRoot(TagSet tags) => tags.Contains(Root);

    /// <summary>
    /// Canonicalize a merge-group key (either a legacy <c>PG-N</c> name or
    /// a Rev 4 <c>mg_path</c> like <c>pg-1/pg-2</c>) into the form embedded
    /// in <see cref="ImplMergedInMgPrefix"/> tags. Lowercased + trimmed so
    /// the C# tag writer and the C# tag reader agree regardless of casing
    /// drift between <c>workflow.input.mg_path</c> (Rev 4: lower-case
    /// <c>pg-N</c>) and <c>workflow.input.pg_name</c> /
    /// <c>resolvedMergeGroup</c> (legacy: upper-case <c>PG-N</c>).
    ///
    /// Returns empty for null / whitespace input — callers should treat
    /// empty as a programmer error and refuse to write or check tags.
    /// </summary>
    public static string NormalizeMergeGroupKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        return key.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Compose the full tag value for the impl-merged-in-mg marker for the
    /// given merge group. Uses <see cref="NormalizeMergeGroupKey(string)"/>
    /// so writer and reader produce the same string regardless of casing.
    /// Returns empty for empty/whitespace input — the caller MUST guard
    /// against this rather than write a malformed bare-prefix tag.
    /// </summary>
    public static string ImplMergedInMg(string mergeGroupKey)
    {
        var normalized = NormalizeMergeGroupKey(mergeGroupKey);
        return normalized.Length == 0
            ? string.Empty
            : $"{ImplMergedInMgPrefix}={normalized}";
    }

    /// <summary>
    /// Returns true if the parsed tag set already carries the
    /// impl-merged-in-mg marker for the given merge group. Always false
    /// for empty/whitespace <paramref name="mergeGroupKey"/> — an empty
    /// key cannot match any tag.
    /// </summary>
    public static bool HasImplMergedInMg(TagSet tags, string mergeGroupKey)
    {
        var tag = ImplMergedInMg(mergeGroupKey);
        if (tag.Length == 0) return false;
        return tags.Contains(tag);
    }

    /// <summary>
    /// Tag-name prefix for the "this apex's current run started at this
    /// ISO-8601 UTC instant" marker, stamped on the apex root by
    /// <c>polyphony reset state</c> (and re-stamped on every subsequent
    /// reset). Observers consume it as a watermark — any merged PR whose
    /// <c>MergedAt</c> is at or before this instant is treated as an
    /// artifact of a prior run and is filtered out of satisfaction
    /// observations. When the tag is ABSENT, no filter is applied —
    /// preserves legacy behavior for apexes that have never been reset.
    ///
    /// Authoritative spec: <c>docs/decisions/run-reset.md</c>.
    /// </summary>
    public const string RunStartedAtPrefix = "polyphony:run-started-at";

    /// <summary>
    /// Compose the full <c>polyphony:run-started-at=&lt;value&gt;</c> tag for
    /// <paramref name="instant"/>. Always serialised as ISO-8601 UTC with
    /// millisecond precision (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>) so the
    /// reader can disambiguate watermark equality from a PR that merged
    /// in the same second — the boundary comparison in
    /// <c>PlanObserver.IsPriorRunMergedPr</c> is <c>&lt;=</c>, so an
    /// imprecise watermark could mis-filter current-run PRs that complete
    /// in the same second as reset.
    /// </summary>
    public static string RunStartedAt(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return $"{RunStartedAtPrefix}={utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Read the <c>polyphony:run-started-at</c> tag value from
    /// <paramref name="tags"/>. Scans ALL matching prefix tags and
    /// returns the MAX-valued parseable value — defensive against
    /// duplicate stamps (PR 2's reset may briefly hold two prefix tags
    /// during the read-write window, and manual operator edits can
    /// introduce duplicates). Returns null when no tag is present OR
    /// when every matching tag's value is unparseable — callers MUST
    /// treat null as "no filter" rather than "filter from epoch zero".
    /// </summary>
    public static DateTimeOffset? ReadRunStartedAt(TagSet tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        const string equalsSep = "=";
        var prefixEq = RunStartedAtPrefix + equalsSep;
        DateTimeOffset? max = null;
        foreach (var tag in tags)
        {
            if (!tag.StartsWith(prefixEq, StringComparison.Ordinal))
                continue;
            var value = tag.Substring(prefixEq.Length);
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                continue;
            }
            if (max is null || parsed > max.Value) max = parsed;
        }
        return max;
    }
}
