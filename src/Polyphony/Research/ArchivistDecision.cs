namespace Polyphony.Research;

/// <summary>
/// A single archivist decision for one scratch artifact. Emitted by the
/// <c>research curate</c> verb (one per artifact in the scratch directory).
/// The <see cref="Decision"/> field is a <see cref="CurationDecision"/>
/// string constant — not an enum — so it serializes as a plain JSON string.
/// </summary>
public sealed record ArchivistDecision
{
    /// <summary>Relative path of the artifact within the scratch directory.</summary>
    public required string ArtifactPath { get; init; }

    /// <summary>
    /// One of <see cref="CurationDecision.Keep"/>,
    /// <see cref="CurationDecision.Discard"/>, or
    /// <see cref="CurationDecision.Expand"/>.
    /// </summary>
    public required string Decision { get; init; }

    /// <summary>Human-readable rationale for the decision.</summary>
    public required string Rationale { get; init; }

    /// <summary>Five-axis relevance assessment.</summary>
    public required RelevanceSignals RelevanceSignals { get; init; }
}
