namespace Polyphony.Sdlc;

/// <summary>
/// Composes the <see cref="AgentAddendum"/> the driver injects into an agent
/// invocation, by unioning the <see cref="FacetProfile"/>s for the item's
/// facet set and pinning the supplied per-item guidance.
/// </summary>
/// <remarks>
/// <para>
/// The composer is permissive at compose-time: identical-value collisions
/// across facets dedupe silently (a skill bound by two facets ends up in the
/// addendum once), and unknown facet names are silently omitted (no
/// exception, no diagnostic). The caller is responsible for ensuring
/// different-value collisions and unknown-facet typos have already been
/// rejected at config-load time — see <see cref="Polyphony.Configuration.FacetProfileValidator"/>.
/// </para>
/// <para>
/// Output ordering is deterministic: <see cref="AgentAddendum.Skills"/> and
/// <see cref="AgentAddendum.Mcps"/> are sorted ascending under the ordinal
/// comparer. This matters for snapshot tests and reviewer rubrics that
/// compare composed addenda byte-for-byte across runs.
/// </para>
/// </remarks>
public static class FacetProfileComposer
{
    /// <summary>
    /// Compose the agent addendum for an item with the given facets.
    /// </summary>
    /// <param name="facets">The item's facet set. May be empty; duplicates
    /// are ignored. Facet names not present in <paramref name="profiles"/>
    /// are silently omitted.</param>
    /// <param name="profiles">The map of facet name → profile, loaded from
    /// the process-config <c>facets:</c> block. May be empty.</param>
    /// <param name="perItemGuidance">Append-only prompt context for this
    /// item. Passed through verbatim onto the result's
    /// <see cref="AgentAddendum.GuidanceContext"/>.</param>
    /// <returns>An addendum with skills and MCPs unioned, deduped, and
    /// sorted ascending; guidance preserved as supplied.</returns>
    public static AgentAddendum Compose(
        IReadOnlyList<string> facets,
        IReadOnlyDictionary<string, FacetProfile> profiles,
        string? perItemGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(facets);
        ArgumentNullException.ThrowIfNull(profiles);

        var skills = new HashSet<string>(StringComparer.Ordinal);
        var mcps = new HashSet<string>(StringComparer.Ordinal);

        foreach (var facet in facets)
        {
            if (facet is null) continue;
            if (!profiles.TryGetValue(facet, out var profile)) continue;
            foreach (var skill in profile.Skills) skills.Add(skill);
            foreach (var mcp in profile.Mcps) mcps.Add(mcp);
        }

        return new AgentAddendum(
            Skills: skills.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            Mcps: mcps.OrderBy(m => m, StringComparer.Ordinal).ToArray(),
            GuidanceContext: perItemGuidance);
    }
}
