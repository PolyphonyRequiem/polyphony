namespace Polyphony.Research;

/// <summary>
/// Five-axis relevance assessment emitted by the archivist for each scratch
/// artifact. Each signal is a short prose string describing relevance (or
/// lack thereof) along that axis. The axes come from the parent Epic #3071
/// specification: domain, codebase, technology stacks, ecosystem, and
/// linkability.
/// </summary>
public sealed record RelevanceSignals
{
    /// <summary>Relevance to the project's problem domain.</summary>
    public required string Domain { get; init; }

    /// <summary>Relevance to the current codebase (patterns, gaps, debt).</summary>
    public required string Codebase { get; init; }

    /// <summary>Relevance to the technology stacks in use.</summary>
    public required string TechnologyStacks { get; init; }

    /// <summary>Relevance to the broader ecosystem (packages, services, standards).</summary>
    public required string Ecosystem { get; init; }

    /// <summary>Degree to which the artifact can be cross-referenced with other kept articles.</summary>
    public required string Linkability { get; init; }
}
