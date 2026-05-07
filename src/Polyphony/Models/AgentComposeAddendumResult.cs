namespace Polyphony.Models;

/// <summary>
/// JSON output of <c>polyphony agent compose-addendum &lt;workItem&gt;</c>.
/// Composes the agent <see cref="Sdlc.AgentAddendum"/> for a work item by
/// looking up the item's facet set on the process config, unioning the
/// matching facet profiles, and pinning the per-item guidance the policy
/// resolves for the item's type.
/// </summary>
/// <remarks>
/// <para>
/// Routing-style envelope: the verb ALWAYS exits
/// <see cref="ExitCodes.Success"/> and surfaces failures via
/// <see cref="Error"/> + <see cref="ErrorCode"/>. Workflow YAML can route
/// on those fields without re-checking the process exit code, mirroring
/// <see cref="EdgesCheckResult"/> and <see cref="StateNextReadyResult"/>.
/// </para>
/// <para>
/// <see cref="Skills"/> and <see cref="Mcps"/> are deduped + sorted
/// ascending under the ordinal comparer (the contract carried over from
/// <see cref="Sdlc.FacetProfileComposer"/>). <see cref="Guidance"/> is
/// passed through verbatim from <see cref="Guidance.GuidanceExtractor"/>;
/// <see cref="GuidancePresent"/> is the boolean projection workflows
/// route on without re-checking the nullable field.
/// </para>
/// </remarks>
public sealed record AgentComposeAddendumResult
{
    /// <summary>The work item the addendum was composed for.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>
    /// Facet set of the work item's type, in the order declared on
    /// <c>process-config.yaml</c>. Echoed back so workflow YAML can
    /// route on the set without a second lookup.
    /// </summary>
    public required IReadOnlyList<string> Facets { get; init; }

    /// <summary>
    /// Skill names unioned across the item's facets, deduped and
    /// sorted ascending (ordinal). Empty when no facet bound a skill.
    /// </summary>
    public required IReadOnlyList<string> Skills { get; init; }

    /// <summary>
    /// MCP server names unioned across the item's facets, deduped and
    /// sorted ascending (ordinal). Empty when no facet bound an MCP.
    /// </summary>
    public required IReadOnlyList<string> Mcps { get; init; }

    /// <summary>
    /// Per-item guidance text (extracted via the resolved
    /// <c>guidance</c> policy for the work item's type), or null when
    /// no guidance is present. Distinguishable from the empty string.
    /// </summary>
    public string? Guidance { get; init; }

    /// <summary>Convenience boolean: true iff <see cref="Guidance"/> is non-null.</summary>
    public required bool GuidancePresent { get; init; }

    /// <summary>Operator-facing error message when the addendum cannot be composed. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>work_item_not_found</c>,
    /// <c>type_unknown</c>, <c>invalid_facet_profile_config</c>,
    /// <c>guidance_misconfigured</c>, <c>cache_error</c>. Null on success.
    /// </summary>
    public string? ErrorCode { get; init; }
}
