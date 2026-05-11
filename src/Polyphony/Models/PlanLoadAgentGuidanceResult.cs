namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan load-agent-guidance</c>. Loads repo-specific agent
/// guidance markdown for the three canonical roles (architect, coder, reviewer),
/// each composed of role-wide guidance plus an optional per-type refinement keyed
/// by the work item's type slug. Also surfaces optional per-named-agent overrides
/// loaded from <c>agent-guidance/agents/&lt;name&gt;.md</c> — when present these
/// REPLACE the role-wide block for that named agent (type refinement still stacks
/// on top). Consumed by the <c>guidance_loader</c> script step in workflow YAMLs;
/// agent prompts read the relevant blocks via Jinja2 conditionals so prompts
/// degrade gracefully when no guidance files exist.
/// </summary>
/// <remarks>
/// Replaces the prior <c>load-guidance</c> verb's flat type-keyed map. The
/// type-keyed model conflated work-item types (Epic / Issue / Task) with agent
/// roles (architect / coder / reviewer); this verb separates the two axes, with
/// the optional <see cref="Agents"/> dimension acting as a per-named-agent
/// escape hatch on top of the role baseline.
/// </remarks>
public sealed record PlanLoadAgentGuidanceResult
{
    /// <summary>Work item type name (e.g. "Epic", "Bug"). Empty when no work item context was resolvable.</summary>
    public required string Type { get; init; }

    public required AgentGuidanceForRole Architect { get; init; }
    public required AgentGuidanceForRole Coder { get; init; }
    public required AgentGuidanceForRole Reviewer { get; init; }

    /// <summary>
    /// Per-named-agent overrides loaded from <c>.polyphony-config/agent-guidance/agents/&lt;name&gt;.md</c>.
    /// Keys are the file basename (without <c>.md</c>) — typically the agent's
    /// step id in workflow YAML (e.g. <c>pr_reviewer</c>, <c>scope_reviewer</c>,
    /// <c>actionable_agent</c>). When present, the agent's prompt should use this
    /// value INSTEAD OF the role-wide baseline; the type refinement still stacks
    /// on top. Empty dict when no overrides exist.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Agents { get; init; }

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
