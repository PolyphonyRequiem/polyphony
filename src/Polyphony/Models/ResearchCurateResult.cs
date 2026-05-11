using Polyphony.Research;

namespace Polyphony.Models;

/// <summary>
/// Result envelope for <c>polyphony research curate</c>. Contains the
/// archivist's per-artifact decisions for all scratch files under the
/// given apex. Routing-style: always exit 0; errors surface via
/// <see cref="Error"/> + <see cref="ErrorCode"/>.
/// </summary>
public sealed record ResearchCurateResult
{
    /// <summary>Apex work-item ID whose scratch directory was curated.</summary>
    public required int ApexId { get; init; }

    /// <summary>Scratch directory path that was enumerated.</summary>
    public required string ScratchDir { get; init; }

    /// <summary>One decision per artifact found in the scratch directory.</summary>
    public required IReadOnlyList<ArchivistDecision> Decisions { get; init; }

    /// <summary>Total artifact count (should equal <see cref="Decisions"/> length on success).</summary>
    public required int ArtifactCount { get; init; }

    /// <summary>Human-readable error message (null on success).</summary>
    public string? Error { get; init; }

    /// <summary>Machine-routable error code (null on success).</summary>
    public string? ErrorCode { get; init; }
}
