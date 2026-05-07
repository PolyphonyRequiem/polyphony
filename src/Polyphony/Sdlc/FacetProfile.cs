namespace Polyphony.Sdlc;

/// <summary>
/// Per-facet profile: the skills + MCPs that get unioned into the agent
/// addendum when an item carries this facet. Populated from the
/// process-config.yaml top-level <c>facets:</c> block, where each entry is
/// keyed by canonical facet name (see <see cref="Facet"/>) and supplies the
/// skill and MCP names the driver should layer onto the agent invocation.
/// </summary>
/// <remarks>
/// <para>
/// Skills and MCPs are referenced by name only — there is no new file format
/// or loading mechanism here. The driver (PR #5 of Phase 6) is responsible
/// for resolving those names to the on-disk skills under <c>.github/skills/</c>
/// and to MCP server entries.
/// </para>
/// <para>
/// Per-item guidance is NOT carried on a profile — it is append-only prompt
/// context and lives on <see cref="AgentAddendum"/> instead.
/// </para>
/// </remarks>
public sealed record FacetProfile(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Mcps);
