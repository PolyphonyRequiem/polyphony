namespace Polyphony.Models;

/// <summary>
/// Result envelope for <c>polyphony research promote</c>. Summarizes the
/// promotion writer's actions: which artifacts were promoted, which were
/// discarded, and which are pending expand for the loop-back pass (#3076).
/// </summary>
public sealed record ResearchPromoteResult
{
    /// <summary>Apex work-item ID.</summary>
    public required int ApexId { get; init; }

    /// <summary>Paths of artifacts promoted (written) to the sibling research repo.</summary>
    public required IReadOnlyList<string> Promoted { get; init; }

    /// <summary>Artifact paths flagged for expand (consumed by #3076 loop-back).</summary>
    public required IReadOnlyList<string> ExpandRequested { get; init; }

    /// <summary>Count of discarded artifacts (no-op — scratch is pruned at apex close).</summary>
    public required int DiscardedCount { get; init; }

    /// <summary>
    /// Platform combination exercised, e.g. <c>"source:github+research:azure_devops"</c>.
    /// Recorded for cross-platform proof traceability.
    /// </summary>
    public required string PlatformCombo { get; init; }

    /// <summary>Human-readable error message (null on success).</summary>
    public string? Error { get; init; }

    /// <summary>Machine-routable error code (null on success).</summary>
    public string? ErrorCode { get; init; }
}
