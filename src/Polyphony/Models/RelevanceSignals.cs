namespace Polyphony;

/// <summary>
/// Five-axis relevance assessment emitted alongside each archivist decision.
/// Axes are drawn from the parent epic #3071's research framing: domain
/// knowledge, codebase alignment, technology stacks, ecosystem context,
/// and linkability to existing knowledge artifacts.
/// </summary>
public sealed record RelevanceSignals
{
    /// <summary>How relevant the artifact is to the domain under research.</summary>
    public required string Domain { get; init; }

    /// <summary>How closely the artifact relates to the target codebase.</summary>
    public required string Codebase { get; init; }

    /// <summary>Alignment with the technology stacks in scope.</summary>
    public required string TechnologyStacks { get; init; }

    /// <summary>Relevance to the broader ecosystem (packages, services, integrations).</summary>
    public required string Ecosystem { get; init; }

    /// <summary>Degree to which the artifact can be linked to existing knowledge.</summary>
    public required string Linkability { get; init; }
}
