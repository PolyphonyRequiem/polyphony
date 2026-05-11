namespace Polyphony;

/// <summary>
/// JSON output of <c>polyphony agent archivist</c>. Routing-style envelope:
/// the verb ALWAYS exits <see cref="ExitCodes.Success"/> and surfaces
/// failures via <see cref="Error"/> + <see cref="ErrorCode"/>.
/// </summary>
/// <remarks>
/// <para>
/// On success, <see cref="Decisions"/> contains one
/// <see cref="ArchivistDecision"/> per artifact found in the apex scratch
/// directory. On error, <see cref="Decisions"/> is empty and
/// <see cref="Error"/>/<see cref="ErrorCode"/> describe the failure.
/// </para>
/// <para>
/// Artifact paths in each decision are relative to the apex scratch root,
/// sorted ascending by ordinal string comparison for deterministic output.
/// </para>
/// </remarks>
public sealed record ArchivistResult
{
    /// <summary>The apex work item ID this archivist run covers.</summary>
    public required int Apex { get; init; }

    /// <summary>Fully resolved scratch directory that was enumerated.</summary>
    public required string ScratchPath { get; init; }

    /// <summary>
    /// Per-artifact decisions, one entry per file found under the apex
    /// scratch directory. Empty on error.
    /// </summary>
    public required IReadOnlyList<ArchivistDecision> Decisions { get; init; }

    /// <summary>Operator-facing error message. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Categorical error code routed by workflow YAML. One of:
    /// <c>invalid_argument</c>, <c>scratch_dir_not_found</c>,
    /// <c>no_artifacts</c>. Null on success.
    /// </summary>
    public string? ErrorCode { get; init; }
}
