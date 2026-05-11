namespace Polyphony;

/// <summary>
/// A single archivist decision for one scratch artifact. The archivist
/// agent emits one of these per artifact found in the apex scratch
/// directory. The <see cref="Decision"/> field is one of
/// <see cref="ArchivistVerdict.Keep"/>, <see cref="ArchivistVerdict.Discard"/>,
/// or <see cref="ArchivistVerdict.Expand"/>; downstream consumers
/// (promotion writer, expand loop) route on it deterministically.
/// </summary>
public sealed record ArchivistDecision
{
    /// <summary>Relative path of the artifact within the apex scratch directory.</summary>
    public required string Artifact { get; init; }

    /// <summary>
    /// The verdict: one of <see cref="ArchivistVerdict.Keep"/>,
    /// <see cref="ArchivistVerdict.Discard"/>, or
    /// <see cref="ArchivistVerdict.Expand"/>.
    /// </summary>
    public required string Decision { get; init; }

    /// <summary>Free-text rationale explaining the verdict.</summary>
    public required string Rationale { get; init; }

    /// <summary>Five-axis relevance assessment for the artifact.</summary>
    public required RelevanceSignals RelevanceSignals { get; init; }
}
