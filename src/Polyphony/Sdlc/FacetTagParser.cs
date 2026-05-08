using Polyphony.Tagging;

namespace Polyphony.Sdlc;

/// <summary>
/// Round-trip helper for the <c>polyphony:facets=&lt;csv&gt;</c> tag that
/// architects can stamp on an apex work item via plan front-matter
/// (<c>apex_facets</c>) when they choose NOT to decompose. The tag is the
/// per-item override consumed by <see cref="RequirementInputResolver"/> so
/// the apex deriver sees an explicit facet set instead of the type-config
/// default.
///
/// <para>The format is intentionally narrow to keep the round-trip
/// deterministic for tests and grep-friendly for humans:</para>
///
/// <list type="bullet">
///   <item>Tag name: <c>polyphony:facets</c> (see <see cref="PolyphonyTags.FacetsPrefix"/>).</item>
///   <item>Tag value: <c>polyphony:facets=&lt;csv&gt;</c>, where <c>&lt;csv&gt;</c> is
///         a comma-separated list of canonical facet name strings
///         (<see cref="Facet"/>) — alphabetical, lowercase, no whitespace,
///         deduplicated.</item>
/// </list>
///
/// <para>All inputs are tolerant on the read path (case-insensitive,
/// whitespace-stripping, deduplicating). The write path always normalises
/// to the canonical shape so the same input produces the same tag.</para>
/// </summary>
public static class FacetTagParser
{
    /// <summary>
    /// Outcome of parsing a candidate <c>polyphony:facets=&lt;csv&gt;</c> value.
    /// Use a discriminated outcome so callers can route on the failure case
    /// (unknown facet name) without inspecting an exception.
    /// </summary>
    public sealed record ParseResult
    {
        /// <summary>True when the value parsed cleanly and every token was a
        /// canonical facet name. False when one or more tokens were unknown.</summary>
        public required bool IsValid { get; init; }

        /// <summary>The normalised facet set on success. Empty on failure.
        /// Always alphabetical, lowercase, deduplicated.</summary>
        public required IReadOnlyList<string> Facets { get; init; }

        /// <summary>The unknown facet tokens that caused failure (verbatim,
        /// in original casing). Empty on success.</summary>
        public required IReadOnlyList<string> UnknownFacets { get; init; }
    }

    /// <summary>
    /// Parses a list of candidate facet name strings (e.g. from plan front-matter
    /// or the comma-separated payload of a <c>polyphony:facets=...</c> tag).
    ///
    /// <para>Tolerant on input: tokens are trimmed, lowercased, deduplicated.
    /// Empty / whitespace-only inputs collapse to an empty result with
    /// <see cref="ParseResult.IsValid"/> = true (same as "no override
    /// declared"). Any token that does not match a canonical
    /// <see cref="Facet"/> name causes <see cref="ParseResult.IsValid"/> to
    /// be false; ALL unknown tokens are reported so the user can fix them
    /// in one round-trip.</para>
    /// </summary>
    public static ParseResult ParseFacets(IEnumerable<string>? tokens)
    {
        if (tokens is null)
        {
            return new ParseResult { IsValid = true, Facets = [], UnknownFacets = [] };
        }

        var unknown = new List<string>();
        var canonical = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var raw in tokens)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            var lower = trimmed.ToLowerInvariant();
            if (!Facet.IsValid(lower))
            {
                unknown.Add(trimmed);
                continue;
            }
            canonical.Add(lower);
        }

        if (unknown.Count > 0)
        {
            return new ParseResult { IsValid = false, Facets = [], UnknownFacets = unknown };
        }

        return new ParseResult { IsValid = true, Facets = canonical.ToArray(), UnknownFacets = [] };
    }

    /// <summary>
    /// Convenience overload that parses the comma-separated value portion of
    /// a <c>polyphony:facets=&lt;csv&gt;</c> tag.
    /// </summary>
    public static ParseResult ParseTagValue(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new ParseResult { IsValid = true, Facets = [], UnknownFacets = [] };
        }
        return ParseFacets(csv.Split(',', StringSplitOptions.None));
    }

    /// <summary>
    /// Formats a facet set as the canonical <c>polyphony:facets=&lt;csv&gt;</c>
    /// tag string. Throws when any token is not a canonical facet — the
    /// write path is intentionally strict so a tagged item never ends up
    /// with garbage on disk.
    /// </summary>
    public static string FormatTag(IEnumerable<string> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);

        var canonical = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in facets)
        {
            if (string.IsNullOrWhiteSpace(f))
            {
                throw new ArgumentException("Facet name cannot be null or whitespace.", nameof(facets));
            }
            var lower = f.Trim().ToLowerInvariant();
            if (!Facet.IsValid(lower))
            {
                throw new ArgumentException(
                    $"Unknown facet '{f}'. Allowed: {Facet.Plannable}, {Facet.Actionable}, {Facet.Implementable}.",
                    nameof(facets));
            }
            canonical.Add(lower);
        }

        return $"{PolyphonyTags.FacetsPrefix}={string.Join(',', canonical)}";
    }

    /// <summary>
    /// Scans an ADO tag set for the <c>polyphony:facets=...</c> override and
    /// returns the parsed facet list when present, or <c>null</c> when no
    /// such tag exists. A malformed value (unknown facet name, etc.)
    /// surfaces as a <see cref="ParseResult"/> with
    /// <see cref="ParseResult.IsValid"/> = false so the caller can fail
    /// loudly rather than silently fall back to the type-config default.
    /// </summary>
    public static ParseResult? TryExtract(TagSet tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var prefix = PolyphonyTags.FacetsPrefix + "=";
        foreach (var tag in tags)
        {
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return ParseTagValue(tag[prefix.Length..]);
            }
        }
        return null;
    }
}
