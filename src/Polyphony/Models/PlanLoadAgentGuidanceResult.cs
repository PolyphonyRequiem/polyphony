namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan load-agent-guidance</c>. Loads repo-specific agent
/// guidance markdown for the three canonical roles (architect, coder, reviewer),
/// each composed of role-wide guidance plus an optional per-type refinement keyed
/// by the work item's type slug. Consumed by the <c>guidance_loader</c> script
/// step in <c>plan-level.yaml</c> and <c>implement-merge-group.yaml</c>; the
/// architect / coder / reviewer agents read their respective <c>role</c> +
/// <c>type_refinement</c> blocks via Jinja2 conditionals so prompts degrade
/// gracefully when no guidance files exist.
/// </summary>
/// <remarks>
/// Replaces the prior <c>load-guidance</c> verb's flat type-keyed map. The
/// type-keyed model conflated work-item types (Epic / Issue / Task) with agent
/// roles (architect / coder / reviewer); this verb separates the two axes.
/// </remarks>
public sealed record PlanLoadAgentGuidanceResult
{
    /// <summary>Work item type name (e.g. "Epic", "Bug"). Empty when no work item context was resolvable.</summary>
    public required string Type { get; init; }

    public required AgentGuidanceForRole Architect { get; init; }
    public required AgentGuidanceForRole Coder { get; init; }
    public required AgentGuidanceForRole Reviewer { get; init; }

    /// <summary>Set when the verb failed to resolve the work item or its type.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Per-role guidance: the role-wide markdown plus an optional per-type refinement.
/// Both fields are empty strings when the corresponding files do not exist.
/// </summary>
public sealed record AgentGuidanceForRole
{
    /// <summary>Contents of <c>.polyphony-config/agent-guidance/&lt;role&gt;.md</c>. Empty when missing.</summary>
    public required string Role { get; init; }

    /// <summary>Contents of <c>.polyphony-config/agent-guidance/&lt;role&gt;/&lt;typeslug&gt;.md</c>. Empty when missing.</summary>
    public required string TypeRefinement { get; init; }
}
