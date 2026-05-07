namespace Polyphony.Configuration;

/// <summary>
/// Config-load-time checks for the top-level <c>facets:</c> block of
/// <c>process-config.yaml</c>. Plugged into <see cref="ConfigValidator"/>
/// (V-20). Run independently in tests via <see cref="Validate"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Skills and MCPs are bound by name only — they're plain
/// strings — so a "different value for the same name" collision is not a
/// thing the validator can detect. What it <i>can</i> detect, and what is
/// almost certainly a typo when it occurs, is a duplicate name within a
/// single facet's <c>skills:</c> or <c>mcps:</c> list. Example:
/// </para>
/// <code>
/// facets:
///   actionable:
///     skills: [evidence, evidence, security]   # duplicate — V-20
/// </code>
/// <para>
/// <b>Cross-facet identical names dedupe silently</b> at compose time (see
/// <see cref="Sdlc.FacetProfileComposer"/>) — that's the whole point of
/// union semantics. The validator does NOT flag them.
/// </para>
/// </remarks>
public static class FacetProfileValidator
{
    /// <summary>The canonical rule id for "duplicate skill or MCP name within a facet".</summary>
    public const string DuplicateWithinFacetRuleId = "V-20";

    /// <summary>
    /// Validate the top-level <c>facets:</c> block on <paramref name="config"/>.
    /// Returns one error per duplicate occurrence found, plus separate errors
    /// for skills versus MCPs. Returns an empty list when the block is absent
    /// or every list is duplicate-free.
    /// </summary>
    public static IReadOnlyList<ConfigValidationDiagnostic> Validate(ProcessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<ConfigValidationDiagnostic>();
        if (config.Facets is null || config.Facets.Count == 0)
        {
            return errors;
        }

        foreach (var (facetName, profile) in config.Facets)
        {
            CheckList(profile.Skills, facetName, "skill", errors);
            CheckList(profile.Mcps, facetName, "mcp", errors);
        }

        return errors;
    }

    private static void CheckList(
        string[] items,
        string facetName,
        string listLabel,
        List<ConfigValidationDiagnostic> errors)
    {
        if (items is null || items.Length < 2) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in items)
        {
            if (name is null) continue;
            if (!seen.Add(name) && reported.Add(name))
            {
                errors.Add(new ConfigValidationDiagnostic
                {
                    RuleId = DuplicateWithinFacetRuleId,
                    Severity = ConfigValidationSeverity.Error,
                    Message =
                        $"Facet '{facetName}' lists {listLabel} '{name}' more than once. " +
                        "Duplicate names within a single facet's skills/mcps list are almost " +
                        "certainly a typo. Cross-facet duplicates are fine — they dedupe at compose time.",
                });
            }
        }
    }
}
